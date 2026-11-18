using System.IO;
using System.Net.Http;

namespace PCL.Modpack;

public sealed class PackDownloadOptions {
    public Func<long, bool>? ApproveWholeArchive { get; set; }
    public Func<string, string, string?>? SelectLocalFile { get; set; }
    public Func<string, bool>? SkipOptional { get; set; }
    public Action<string, Stream, long, long, long>? DownloadRange { get; set; }
    public PackMirror? Mirror { get; set; }
}

public sealed partial class PackUpdater {
    private PackPlan PlanArchive(PackPlan plan) {
        if (plan.IsApplied) return plan;
        var previous = ((JArray?)plan.State["files"] ?? (JArray)plan.Release["baseline"]!).Cast<JObject>().ToList();
        var previousByPath = previous.ToDictionary(f => (string)f["path"]!, StringComparer.OrdinalIgnoreCase);
        var previousById = previous.Where(f => f["id"] != null).ToLookup(f => (string)f["id"]!, StringComparer.Ordinal);
        var baselineByPath = plan.Release["baseline"]!.Cast<JObject>().ToDictionary(f => (string)f["path"]!, StringComparer.OrdinalIgnoreCase);
        JObject? ModRecord(string path) {
            string enabled = PackProtocol.EnabledModPath(path);
            return previousByPath.TryGetValue(enabled, out var record) || previousByPath.TryGetValue(enabled + ".disabled", out record) ? record : null;
        }
        bool hasLegacyMods = previous.Any(f => f["id"] == null && PackProtocol.ManagedMod((string)f["path"]!));
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? LocalHash(string path) {
            if (!hashes.TryGetValue(path, out var hash)) hashes.Add(path, hash = Hash(path));
            return hash;
        }
        if (plan.State["files"] == null) plan.BaselineMatches = previous.Count(f => {
            string path = (string)f["path"]!;
            string? actual = FindLocal(path, LocalHash);
            return actual != null && LocalHash(actual) == (string?)f["sha256"];
        });
        var localMods = new Lazy<ILookup<string, string>>(ReadLocalModIds);
        string? ById(string? id) {
            if (id == null) return null;
            var matches = localMods.Value[id].Take(2).ToList();
            if (matches.Count > 1) throw new IOException("有多个本地文件使用同一 Mod ID，请先选择保留哪一个：" + id);
            return matches.FirstOrDefault();
        }
        var targets = new HashSet<string>(plan.Release["files"]!.Select(f => (string)f["path"]!), StringComparer.OrdinalIgnoreCase);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = plan.Release["actions"]!.Cast<JObject>().Where(a => Included(a, plan.Selected)).ToList();
        foreach (var action in actions) handled.Add((string)action["path"]!);
        foreach (JObject file in plan.Release["files"]!.Where(f => Included(f, plan.Selected))) {
            string requested = (string)file["path"]!;
            string actual = requested;
            string? found = FindLocal(requested, LocalHash);
            string? extraOld = null;
            JObject? old = previousByPath.TryGetValue(requested, out var prior) ? prior : null;
            if (PackProtocol.ManagedMod(requested)) {
                string enabled = PackProtocol.EnabledModPath(requested);
                old ??= ModRecord(requested);
                if (old == null && file["id"]?.Value<string>() is string id) {
                    var previousMatches = previousById[id].Take(2).ToList();
                    if (previousMatches.Count == 1) old = previousMatches[0];
                    if (old == null && hasLegacyMods) {
                        var candidates = localMods.Value[id].Select(ModRecord).Where(f => f != null && f["id"] == null).Take(2).ToList();
                        if (candidates.Count > 1) throw new IOException("多个旧受管文件使用同一 Mod ID，请先选择保留哪一个：" + id);
                        old = candidates.FirstOrDefault();
                    }
                }
                string? oldPath = (string?)old?["path"];
                if (oldPath != null) {
                    handled.Add(oldPath);
                    string? priorLocal = FindLocal(oldPath, LocalHash);
                    if (priorLocal != null && found != null && !priorLocal.Equals(found, StringComparison.OrdinalIgnoreCase)) {
                        if (LocalHash(priorLocal) != (string?)old!["sha256"] || LocalHash(found) != (string?)file["sha256"])
                            throw new IOException("新旧 Mod 同时存在，且内容与清单不一致，请先选择保留哪一个：" + priorLocal + "、" + found);
                        extraOld = priorLocal;
                    }
                    found ??= priorLocal;
                }
                found ??= ById((string?)file["id"]);
                string? priorStatePath = extraOld ?? found;
                if ((bool?)file["force_disabled"] != true && priorStatePath != null)
                    actual = enabled + (priorStatePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? ".disabled" : "");
            }
            if (found != null) handled.Add(found);
            handled.Add(requested);
            handled.Add(actual);

            string? localHash = found == null ? null : LocalHash(found);
            string? destinationHash = found != null && found.Equals(actual, StringComparison.OrdinalIgnoreCase) ? localHash : LocalHash(actual);
            string targetHash = (string)file["sha256"]!;
            if (old == null && found != null && PackProtocol.ManagedMod(requested)) {
                string enabled = PackProtocol.EnabledModPath(requested);
                if ((baselineByPath.TryGetValue(enabled, out var baseline) || baselineByPath.TryGetValue(enabled + ".disabled", out baseline)) &&
                    localHash == (string?)baseline["sha256"]) old = baseline;
            }
            string? oldHash = (string?)old?["sha256"];
            bool force = (bool?)file["force"] == true || (bool?)file["force_disabled"] == true || file["json_patch"] != null;
            string policy = (string)file["write_policy"]!;
            if (extraOld != null && !targets.Contains(extraOld) && !actions.Any(a => string.Equals((string?)a["path"], extraOld, StringComparison.OrdinalIgnoreCase)))
                AddAt(plan, file, extraOld, "删除旧版本", LocalHash(extraOld), null, actual);
            if (old != null && PartialConfig(plan, old) && !force && localHash != null) {
                plan.Preserved.Add(requested + "（保留玩家配置）");
                plan.RetainedRecords.Add(old);
                continue;
            }
            if (found == null && old != null && policy == "unmodified" && !force) {
                plan.Preserved.Add(requested + "（本地已移除）");
                plan.RetainedRecords.Add(old);
                continue;
            }
            if (localHash != null && (policy == "preserve" ||
                policy == "unmodified" && !force && localHash != oldHash && localHash != targetHash)) {
                plan.Preserved.Add(found!);
                continue;
            }
            bool renamed = found != null && !found.Equals(actual, StringComparison.OrdinalIgnoreCase);
            if (renamed && destinationHash != null && destinationHash != targetHash && !force) {
                plan.Preserved.Add(found!);
                continue;
            }
            plan.FileTargets[requested] = actual;
            if ((bool?)file["optional"] == true && found != null && old != null) {
                var retained = (JObject)old.DeepClone();
                if (extraOld == null) retained["path"] = found;
                plan.PriorForOptional[actual] = retained;
            }
            if (renamed && localHash == targetHash && PackProtocol.ManagedMod(actual) &&
                PackProtocol.EnabledModPath(actual).Equals(PackProtocol.EnabledModPath(found!), StringComparison.OrdinalIgnoreCase)) {
                AddAt(plan, file, found!, actual.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "禁用" : "启用", localHash, null);
                continue;
            }
            if (destinationHash != targetHash) {
                AddAt(plan, file, actual, file["json_patch"] != null ? "修改配置项" : force ? "强制更新" : destinationHash == null ? "新增" : "更新", destinationHash, file);
                if (destinationHash != null && old != null && PartialConfig(plan, old) && force && file["json_patch"] == null)
                    plan.Changes.Last().Reason = "发布者要求整文件替换，将覆盖玩家的其他配置项。";
            }
            if (renamed && !targets.Contains(found!) && !actions.Any(a => string.Equals((string?)a["path"], found, StringComparison.OrdinalIgnoreCase)))
                AddAt(plan, file, found!, "删除旧版本", localHash, null, actual);
        }
        foreach (var action in actions) {
            string path = (string)action["path"]!;
            bool disable = (string?)action["action"] == "disable";
            bool force = (bool?)action["force"] == true;
            string? found = disable ? (LocalHash(path) != null ? path : null) : FindLocal(path, LocalHash);
            if (found == null && !disable && force) found = ById((string?)action["id"]);
            if (found == null) continue;
            if (plan.FileTargets.Values.Contains(found, StringComparer.OrdinalIgnoreCase))
                throw new IOException("当前发布同时要求保留与删除同一 Mod：" + found);
            handled.Add(found);
            string? hash = LocalHash(found);
            if (hash == null) continue;
            if (hash != (string?)action["expected_sha256"] && !force) { plan.Preserved.Add(found); continue; }
            if (disable && LocalHash(path + ".disabled") is string disabled && disabled != hash)
                throw new IOException("禁用目标已存在且内容不同：" + path + ".disabled");
            AddAt(plan, action, found, disable ? "禁用" : force ? "强制删除" : "删除", hash, null);
        }
        foreach (var old in previous.Where(f => Included(f, CleanupChoice(plan)))) {
            string path = (string)old["path"]!;
            if (handled.Contains(path) || targets.Contains(path)) continue;
            string? found = FindLocal(path, LocalHash);
            if (found == null || !handled.Add(found)) continue;
            string? hash = LocalHash(found);
            if (hash == null) continue;
            if (PartialConfig(plan, old)) {
                plan.Preserved.Add(found);
                plan.RetainedRecords.Add(old);
            } else if (hash == (string?)old["sha256"] && string.Equals(path, found, StringComparison.OrdinalIgnoreCase))
                AddAt(plan, old, found, "删除旧文件", hash, null);
            else plan.Preserved.Add(found);
        }
        if (plan.Changes.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Changes.Count)
            throw new IOException("多个更新操作指向同一个文件。");
        return plan;
    }

    private static string? FindLocal(string path, Func<string, string?> hash) {
        if (!PackProtocol.ManagedMod(path)) return hash(path) != null ? path : null;
        string active = PackProtocol.EnabledModPath(path), disabled = active + ".disabled";
        bool hasActive = hash(active) != null, hasDisabled = hash(disabled) != null;
        if (hasActive && hasDisabled) throw new IOException("同时存在启用和禁用的 Mod，请先选择保留哪一个：" + active);
        return hasActive ? active : hasDisabled ? disabled : null;
    }

    private static bool PartialConfig(PackPlan plan, JObject record) {
        string path = (string)record["path"]!;
        // Older archive states did not distinguish merged configs from whole-file installs.
        // v1 always serialized the component property (null for the main pack).
        // v2 omitted absent components; its schema permits only strings for present ones.
        return record["json_patch"] != null || plan.State["files"] != null && record["whole_file"] == null &&
            record["component"]?.Type != JTokenType.Null &&
            path.StartsWith("config/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private ILookup<string, string> ReadLocalModIds() {
        string mods = PackProtocol.Safe(root, "mods");
        var paths = Directory.Exists(mods) ? Directory.EnumerateFiles(mods).Select(f => "mods/" + Path.GetFileName(f)) : Enumerable.Empty<string>();
        return paths.Where(PackProtocol.ManagedMod).Select(path => (Path: path, Id: PackPublisher.ReadModId(PackProtocol.Safe(root, path))))
            .Where(mod => mod.Id != null).ToLookup(mod => mod.Id!, mod => mod.Path, StringComparer.Ordinal);
    }

    private static void AddAt(PackPlan plan, JObject item, string target, string kind, string? hash, JObject? file, string? dependsOn = null) {
        Add(plan, item, kind, hash, file);
        plan.Changes.Last().Path = target;
        plan.Changes.Last().DependsOn = dependsOn;
    }

    private void RecordArchiveOwnership(PackPlan plan, JArray owned, HashSet<string> skipped) {
        foreach (var choice in plan.RetainedRecords.Concat(plan.PriorForOptional.Where(pair => skipped.Contains(pair.Key)).Select(pair => pair.Value))) {
            if (!owned.Any(f => string.Equals((string?)f["path"], (string?)choice["path"], StringComparison.OrdinalIgnoreCase)))
                owned.Add(choice.DeepClone());
        }
        foreach (JObject file in plan.Release["files"]!.Where(f => Included(f, plan.Selected))) {
            if ((string?)file["write_policy"] == "preserve") continue;
            if (!plan.FileTargets.TryGetValue((string)file["path"]!, out string target) || skipped.Contains(target)) continue;
            string? actual = Hash(target);
            if (actual == null || actual != (string?)file["sha256"] && file["json_patch"] == null) continue;
            var record = new JObject { ["path"] = target, ["sha256"] = actual };
            if (file["json_patch"] != null) record["json_patch"] = file["json_patch"]!.DeepClone();
            else record["whole_file"] = true;
            foreach (string field in new[] { "id", "component" })
                if (file[field] != null) record[field] = file[field]!.DeepClone();
            owned.Add(record);
        }
    }

    private bool StageArchiveFile(PackChange change, PackArchive archive, Dictionary<PackChange, string> staged,
        Action<string> progress, Action<string, string, long>? download, PackDownloadOptions? options) {
        var file = change.File!;
        long size = (long)file["size"]!;
        string sha = (string)file["sha256"]!;
        string cache = Work("downloads/" + sha + ".file");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        if (!PackArchive.Matches(cache, size, sha)) {
            string temporary = Work("stage/" + sha + ".part");
            Exception? failure = null;
            void Verify() {
                if (!PackArchive.Matches(temporary, size, sha)) throw new InvalidDataException("文件大小或 SHA-256 不符。");
                if (System.IO.File.Exists(cache)) System.IO.File.Delete(cache);
                System.IO.File.Move(temporary, cache);
            }
            bool Attempt(Action action) {
                try { action(); Verify(); return true; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException) {
                    failure = ex;
                    return false;
                }
            }
            bool ready = false;
            foreach (string url in file["urls"]!.Select(item => (string)item!)) {
                for (int attempt = 0; attempt < 2 && !ready; attempt++) {
                    progress("正在从平台下载：" + change.Name);
                    ready = Attempt(() => {
                        if (download != null) download(url, temporary, size);
                        else {
                            using var output = System.IO.File.Create(temporary);
                            PackProtocol.Download(url, output, size, null);
                        }
                    });
                }
                if (ready) break;
            }
            if (!ready && file["entry"] != null) {
                progress("正在读取整合包 ZIP：" + change.Name);
                ready = Attempt(() => archive.Extract(file, temporary, Work("stage/compressed.part")));
            }
            if (!ready && options?.SelectLocalFile?.Invoke(change.Path, failure?.Message ?? "") is string source && source.Length > 0) {
                ready = Attempt(() => {
                    using var input = System.IO.File.OpenRead(source);
                    using var output = System.IO.File.Create(temporary);
                    PackArchive.CopyBounded(input, output, size);
                });
            }
            if (!ready) {
                if ((bool?)file["optional"] == true && options?.SkipOptional?.Invoke(change.Path) == true) return false;
                throw new IOException("无法取得文件 " + change.Path + "：" + (failure?.Message ?? "没有可用下载源。"), failure);
            }
        }
        staged[change] = cache;
        if (file["json_patch"] != null && change.Before != null) {
            string merged = Work("stage/merged-" + staged.Count);
            PackJsonPatch.Apply(PackProtocol.Safe(root, change.Path), cache, merged, file["json_patch"]!["keys"]!.Select(item => (string)item!));
            staged[change] = merged;
        }
        return true;
    }
}
