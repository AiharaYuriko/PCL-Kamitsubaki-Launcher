using System.IO;
using System.Net.Http;

namespace PCL.Modpack;

public sealed class PackDownloadOptions {
    public PackDownloadControl? Control { get; set; }
    public Action<PackTransferProgress>? ReportTransfer { get; set; }
    public Func<long, bool>? ApproveWholeArchive { get; set; }
    public Func<string, string, string?>? SelectLocalFile { get; set; }
    public Func<string, bool>? SkipOptional { get; set; }
    public Action<string, Stream, long, long, long>? DownloadRange { get; set; }
    public PackMirror? Mirror { get; set; }
}

public sealed partial class PackUpdater {
    private PackPlan PlanArchive(PackPlan plan) {
        bool repairing = plan.IsCurrentArchiveRelease;
        if (repairing && plan.State["selected"] is JArray installedComponents)
            plan.Selected = new HashSet<string>(installedComponents.Values<string>()!);
        var previous = ((JArray?)plan.State["files"] ?? (JArray)plan.Release["baseline"]!).Cast<JObject>().ToList();
        var previousByPath = previous.ToDictionary(f => (string)f["path"]!, StringComparer.OrdinalIgnoreCase);
        var previousById = previous.ToLookup(PackProtocol.ModIdentity, StringComparer.Ordinal);
        var baselineByPath = plan.Release["baseline"]!.Cast<JObject>().ToDictionary(f => (string)f["path"]!, StringComparer.OrdinalIgnoreCase);
        JObject? ModRecord(string path) {
            string enabled = PackProtocol.EnabledModPath(path);
            return previousByPath.TryGetValue(enabled, out var record) || previousByPath.TryGetValue(enabled + ".disabled", out record) ? record : null;
        }
        bool hasLegacyMods = previous.Any(f => PackProtocol.ModIdentity(f) == null && PackProtocol.ManagedMod((string)f["path"]!));
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? LocalHash(string path) {
            if (!hashes.TryGetValue(path, out var hash)) hashes.Add(path, hash = Hash(path));
            return hash;
        }
        if (plan.State["files"] == null) plan.BaselineMatches = previous.Count(f => {
            string path = (string)f["path"]!;
            string? expected = (string?)f["sha256"];
            if (!PackProtocol.ManagedMod(path)) return LocalHash(path) == expected;
            string enabled = PackProtocol.EnabledModPath(path);
            return LocalHash(enabled) == expected || LocalHash(enabled + ".disabled") == expected;
        });
        var localJars = new Lazy<Dictionary<string, string?>>(ReadLocalModIds);
        var localMods = new Lazy<ILookup<string, string>>(() => localJars.Value
            .Where(jar => jar.Value != null).ToLookup(jar => jar.Value!, jar => jar.Key, StringComparer.Ordinal));
        string? ById(string? id) {
            if (id == null) return null;
            var matches = localMods.Value[id].Take(2).ToList();
            if (matches.Count > 1) throw new IOException("有多个本地文件使用同一 Mod ID，请先选择保留哪一个：" + id);
            return matches.FirstOrDefault();
        }
        var targets = new HashSet<string>(plan.Release["files"]!.Select(f => (string)f["path"]!), StringComparer.OrdinalIgnoreCase);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = plan.Release["actions"]!.Cast<JObject>().Where(a => !repairing && Included(a, plan.Selected)).ToList();
        var actionModPaths = new HashSet<string>(actions.Select(a => (string)a["path"]!).Where(PackProtocol.ManagedMod)
            .Select(PackProtocol.EnabledModPath), StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions) handled.Add((string)action["path"]!);
        var selectedFiles = plan.Release["files"]!.Cast<JObject>()
            .Where(f => Included(f, plan.Selected) && (!repairing || PackProtocol.ManagedMod((string)f["path"]!))).ToList();
        var modTargets = new HashSet<string>(selectedFiles.Select(f => (string)f["path"]!).Where(PackProtocol.ManagedMod)
            .Select(PackProtocol.EnabledModPath), StringComparer.OrdinalIgnoreCase);
        var claimedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in selectedFiles.Select(PackProtocol.ModIdentity).OfType<string>())
            if (!claimedIds.Add(id)) throw new IOException("更新清单为同一 Mod ID 指定了多个目标文件：" + id);
        var platformMatches = new Lazy<ILookup<string, string>>(() => MatchModProjects(plan, selectedFiles, localJars, claimedIds, LocalHash));
        var targetById = new Dictionary<string, string>(StringComparer.Ordinal);
        var replacementByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject file in selectedFiles) {
            string requested = (string)file["path"]!;
            bool isMod = PackProtocol.ManagedMod(requested);
            bool forceDisabled = !repairing && (bool?)file["force_disabled"] == true;
            string? managedId = PackProtocol.ModIdentity(file);
            var platformCandidates = file["sources"] != null && (string?)file["write_policy"] != "preserve"
                ? platformMatches.Value[requested] : Enumerable.Empty<string>();
            if ((string?)file["write_policy"] != "preserve" && (managedId != null || platformCandidates.Any())) {
                var candidates = (managedId == null ? platformCandidates : localMods.Value[managedId].Concat(platformCandidates))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
                string enabled = PackProtocol.EnabledModPath(requested);
                var priorPaths = candidates.Where(path => ModRecord(path) is JObject record && LocalHash(path) == (string?)record["sha256"])
                    .Take(2).ToList();
                bool keepEnabled = priorPaths.Count == 1
                    ? !priorPaths[0].EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    : candidates.Any(path => !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
                string actualTarget = candidates.Count == 0 || forceDisabled
                    ? requested
                    : keepEnabled ? enabled : enabled + ".disabled";
                string? existing = LocalHash(actualTarget);
                string targetSha = (string)file["sha256"]!;
                bool forced = (bool?)file["force"] == true || forceDisabled;

                if (managedId != null) targetById.Add(managedId, actualTarget);
                plan.FileTargets[requested] = actualTarget;
                handled.Add(requested);
                handled.Add(actualTarget);
                if (managedId != null)
                    foreach (var record in previousById[managedId]) handled.Add((string)record["path"]!);
                if (existing != null && existing != targetSha && !candidates.Contains(actualTarget, StringComparer.OrdinalIgnoreCase)) {
                    if (localJars.Value.TryGetValue(actualTarget, out var occupant) && occupant != null)
                        throw new IOException("清单 Mod 目标路径已被其他 Mod 占用：" + actualTarget);
                    if (!forced && existing != (string?)ModRecord(actualTarget)?["sha256"]) {
                        handled.UnionWith(candidates);
                        plan.Preserved.Add(actualTarget);
                        continue;
                    }
                }
                if ((bool?)file["optional"] == true) {
                    var priorRecords = candidates.Select(ModRecord).OfType<JObject>().Distinct().ToList();
                    if (priorRecords.Count != 0) plan.PriorForOptional[actualTarget] = priorRecords;
                }
                string? toggleSource = null;
                if (existing == null) {
                    string otherState = actualTarget.Equals(enabled, StringComparison.OrdinalIgnoreCase) ? enabled + ".disabled" : enabled;
                    if (LocalHash(otherState) == targetSha) {
                        toggleSource = otherState;
                        AddAt(plan, file, otherState, actualTarget.Equals(enabled, StringComparison.OrdinalIgnoreCase) ? "启用" : "禁用", targetSha, null);
                    }
                }
                if (toggleSource == null && existing != targetSha) {
                    string kind = forced ? "强制更新" : existing != null || candidates.Count != 0 ? "更新" :
                        repairing || ModRecord(requested) != null ? "补装" : "新增";
                    AddAt(plan, file, actualTarget, kind, existing, file);
                }
                foreach (string candidate in candidates) {
                    handled.Add(candidate);
                    string candidatePath = PackProtocol.EnabledModPath(candidate);
                    if (candidate.Equals(actualTarget, StringComparison.OrdinalIgnoreCase) || candidate.Equals(toggleSource, StringComparison.OrdinalIgnoreCase)) continue;
                    if (modTargets.Contains(candidatePath) && !candidatePath.Equals(enabled, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("更新清单同时要求保留与替换同一 Mod 文件：" + candidate);
                    if (actionModPaths.Contains(candidatePath)) {
                        replacementByPath[candidatePath] = actualTarget;
                        continue;
                    }
                    AddAt(plan, file, candidate, "删除旧版本", LocalHash(candidate), null, actualTarget);
                }
                continue;
            }
            string actual = requested;
            string? found = FindLocal(requested, LocalHash);
            string? extraOld = null;
            JObject? old = previousByPath.TryGetValue(requested, out var prior) ? prior : null;
            if (isMod) {
                string enabled = PackProtocol.EnabledModPath(requested);
                old ??= ModRecord(requested);
                if (old == null && managedId is string id) {
                    var previousMatches = previousById[id].Take(2).ToList();
                    if (previousMatches.Count == 1) old = previousMatches[0];
                    if (old == null && hasLegacyMods) {
                        var candidates = localMods.Value[id].Select(ModRecord).Where(f => f != null && PackProtocol.ModIdentity(f) == null).Take(2).ToList();
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
                found ??= ById(managedId);
                string? priorStatePath = extraOld ?? found;
                if (!forceDisabled && priorStatePath != null)
                    actual = enabled + (priorStatePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? ".disabled" : "");
            }
            if (found != null) handled.Add(found);
            handled.Add(requested);
            handled.Add(actual);

            string? localHash = found == null ? null : LocalHash(found);
            string? destinationHash = found != null && found.Equals(actual, StringComparison.OrdinalIgnoreCase) ? localHash : LocalHash(actual);
            string targetHash = (string)file["sha256"]!;
            if (old == null && found != null && isMod) {
                string enabled = PackProtocol.EnabledModPath(requested);
                if ((baselineByPath.TryGetValue(enabled, out var baseline) || baselineByPath.TryGetValue(enabled + ".disabled", out baseline)) &&
                    localHash == (string?)baseline["sha256"]) old = baseline;
            }
            string? oldHash = (string?)old?["sha256"];
            bool force = (bool?)file["force"] == true || forceDisabled || file["json_patch"] != null;
            string policy = (string)file["write_policy"]!;
            if (extraOld != null && !targets.Contains(extraOld) && !actions.Any(a => string.Equals((string?)a["path"], extraOld, StringComparison.OrdinalIgnoreCase)))
                AddAt(plan, file, extraOld, "删除旧版本", LocalHash(extraOld), null, actual);
            if (old != null && PartialConfig(plan, old) && !force && localHash != null) {
                plan.Preserved.Add(requested + "（保留玩家配置）");
                plan.RetainedRecords.Add(old);
                continue;
            }
            if (!isMod && found == null && old != null && policy == "unmodified" && !force) {
                plan.Preserved.Add(requested + "（本地已移除）");
                plan.RetainedRecords.Add(old);
                continue;
            }
            if (localHash != null && (policy == "preserve" ||
                (!isMod || old == null) && policy == "unmodified" && !force && localHash != oldHash && localHash != targetHash)) {
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
                plan.PriorForOptional[actual] = new List<JObject> { retained };
            }
            if (renamed && localHash == targetHash && PackProtocol.ManagedMod(actual) &&
                PackProtocol.EnabledModPath(actual).Equals(PackProtocol.EnabledModPath(found!), StringComparison.OrdinalIgnoreCase)) {
                AddAt(plan, file, found!, actual.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "禁用" : "启用", localHash, null);
                continue;
            }
            if (destinationHash != targetHash) {
                bool missingKnownMod = isMod && found == null && (old != null || repairing);
                bool updating = destinationHash != null || isMod && found != null ||
                    missingKnownMod && oldHash != null && oldHash != targetHash;
                string kind = file["json_patch"] != null ? "修改配置项" : force ? "强制更新" :
                    updating ? "更新" : missingKnownMod ? "补装" : "新增";
                AddAt(plan, file, actual, kind, destinationHash, file);
                if (missingKnownMod && plan.Changes.Last().Reason == "")
                    plan.Changes.Last().Reason = "本地缺少旧文件，将安装目标版本。";
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
            replacementByPath.TryGetValue(PackProtocol.EnabledModPath(path), out string? replacement);
            string? actionId = PackProtocol.ModIdentity(action);
            if (replacement == null && actionId != null) targetById.TryGetValue(actionId, out replacement);
            if (found == null && !disable && force && replacement == null) found = ById(actionId);
            if (found == null) continue;
            if (plan.FileTargets.Values.Contains(found, StringComparer.OrdinalIgnoreCase))
                throw new IOException("当前发布同时要求保留与删除同一 Mod：" + found);
            handled.Add(found);
            string? hash = LocalHash(found);
            if (hash == null) continue;
            if (hash != (string?)action["expected_sha256"] && !force) { plan.Preserved.Add(found); continue; }
            if (disable && LocalHash(path + ".disabled") is string disabled && disabled != hash)
                throw new IOException("禁用目标已存在且内容不同：" + path + ".disabled");
            AddAt(plan, action, found, disable ? "禁用" : force ? "强制删除" : "删除", hash, null, replacement);
        }
        if (repairing) {
            plan.RetainedRecords.AddRange(previous.Where(f => !handled.Contains((string)f["path"]!)));
        }
        foreach (var old in previous.Where(f => !repairing && Included(f, CleanupChoice(plan)))) {
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

    private Dictionary<string, string?> ReadLocalModIds() {
        string mods = PackProtocol.Safe(root, "mods");
        var paths = Directory.Exists(mods) ? Directory.EnumerateFiles(mods).Select(f => "mods/" + Path.GetFileName(f)) : Enumerable.Empty<string>();
        return paths.Where(PackProtocol.ManagedMod).ToDictionary(path => path, path => {
            var mod = PackPublisher.ReadPrimaryMod(PackProtocol.Safe(root, path));
            return mod.HasValue ? (mod.Value.Fabric ? "fabric/" : "forge/") + mod.Value.Id : null;
        }, StringComparer.OrdinalIgnoreCase);
    }

    private ILookup<string, string> MatchModProjects(PackPlan plan, List<JObject> files, Lazy<Dictionary<string, string?>> localJars,
        HashSet<string> claimedIds, Func<string, string?> localHash) {
        var targets = files.Where(file => PackProtocol.ManagedMod((string)file["path"]!) && (string?)file["write_policy"] != "preserve")
            .Select(file => (File: file, Identity: PackProtocol.ModIdentity(file), Projects: PackProtocol.ModProjects(file["sources"]).ToArray()))
            .Where(target => target.Projects.Length != 0).ToArray();
        var matches = new List<(string Target, string Local)>();
        if (targets.Length == 0) return matches.ToLookup(match => match.Target, match => match.Local);
        bool hasTargetWithoutId = targets.Any(target => target.Identity == null);
        var known = targets.ToLookup(target => (string)target.File["sha256"]!, target => target.Projects, StringComparer.Ordinal);
        var candidates = localJars.Value.Where(jar => jar.Value == null || hasTargetWithoutId && !claimedIds.Contains(jar.Value))
            .Select(jar => (Path: jar.Key, Identity: jar.Value, Hash: localHash(jar.Key))).Where(jar => jar.Hash != null).ToArray();
        var cache = plan.ModProjectsByHash;
        var unresolved = new List<PackPublishFile>();
        foreach (var group in candidates.GroupBy(jar => jar.Hash!, StringComparer.Ordinal).Where(group => !cache.ContainsKey(group.Key))) {
            if (known.Contains(group.Key)) {
                cache.Add(group.Key, known[group.Key].SelectMany(projects => projects).Distinct(StringComparer.Ordinal).ToArray());
                continue;
            }
            string path = group.First().Path;
            string fullPath = PackProtocol.Safe(root, path);
            unresolved.Add(new PackPublishFile { Path = path, LocalPath = fullPath, Sha256 = group.Key,
                Size = new FileInfo(fullPath).Length, Category = "mod" });
        }
        if (unresolved.Count != 0 && resolveModSources != null) {
            try { resolveModSources(unresolved); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { }
            foreach (var file in unresolved) {
                if (Hash(file.Path) != file.Sha256) throw new IOException("识别 Mod 期间文件已变化，请重新检查更新：" + file.Path);
                cache[file.Sha256] = PackProtocol.ModProjects(file.Sources).ToArray();
            }
        }
        foreach (var candidate in candidates) {
            if (!cache.TryGetValue(candidate.Hash!, out var projects) || projects.Length == 0) continue;
            var owners = targets.Where(target => (target.Identity == null || candidate.Identity == null) &&
                target.Projects.Intersect(projects, StringComparer.Ordinal).Any()).Take(2).ToArray();
            if (owners.Length == 1) matches.Add(((string)owners[0].File["path"]!, candidate.Path));
        }
        return matches.ToLookup(match => match.Target, match => match.Local, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddAt(PackPlan plan, JObject item, string target, string kind, string? hash, JObject? file, string? dependsOn = null) {
        Add(plan, item, kind, hash, file);
        plan.Changes.Last().Path = target;
        plan.Changes.Last().DependsOn = dependsOn;
    }

    private void RecordArchiveOwnership(PackPlan plan, JArray owned, HashSet<string> skipped) {
        foreach (var choice in plan.RetainedRecords.Concat(plan.PriorForOptional.Where(pair => skipped.Contains(pair.Key)).SelectMany(pair => pair.Value))) {
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
            foreach (string field in new[] { "id", "ids", "fabric_id", "component" })
                if (file[field] != null) record[field] = file[field]!.DeepClone();
            owned.Add(record);
        }
    }

    private bool StageArchiveFile(PackChange change, PackArchive archive, Dictionary<PackChange, string> staged,
        Action<string> progress, Action<string, string, long>? download, PackDownloadOptions? options, Action<PackTransferProgress> reportTransfer) {
        var file = change.File!;
        var control = options?.Control;
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
                control?.ThrowIfCanceled();
                try { action(); Verify(); return true; }
                catch (Exception ex) when (ex is not PackUpdateCanceledException &&
                    ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException) {
                    failure = ex;
                    return false;
                }
            }
            bool ready = false;
            bool hasEntry = file["entry"] is JObject;
            var urls = file["urls"]!.Values<string>().ToArray();
            for (int round = 0; round < DownloadRounds && !ready; round++) {
                string title = round == 0 ? change.Name : $"{change.Name}（第 {round + 1} 轮）";
                for (int route = 0; route < urls.Length; route++) {
                    string url = urls[route]!;
                    bool canSwitch = hasEntry || route + 1 < urls.Length || (urls.Length > 1 && round + 1 < DownloadRounds);
                    string label = "正在从 " + PackChange.DownloadSourceFor(url) + " 下载：" + title;
                    progress(label);
                    ready = Attempt(() => {
                        if (download != null) download(url, temporary, size);
                        else {
                            using var output = System.IO.File.Create(temporary);
                            PackProtocol.Download(url, output, size, label, reportTransfer, control: control, canSwitch: canSwitch);
                        }
                    });
                    if (ready) break;
                }
                if (!ready && hasEntry) {
                    progress("正在读取" + PackChange.ArchiveDownloadSource + "：" + title);
                    bool moreRoutes = round + 1 < DownloadRounds && (urls.Length > 0 || options?.Mirror != null);
                    ready = Attempt(() => archive.ExtractRange(file, temporary, Work("stage/compressed.part"), title, moreRoutes));
                }
            }
            if (!ready && hasEntry && failure != null) {
                ready = Attempt(() => archive.FetchWhole(file, temporary, failure));
            }
            control?.ThrowIfCanceled();
            if (!ready && options?.SelectLocalFile?.Invoke(change.Path, failure?.Message ?? "") is string source && source.Length > 0) {
                ready = Attempt(() => {
                    using var input = System.IO.File.OpenRead(source);
                    using var output = System.IO.File.Create(temporary);
                    PackArchive.CopyBounded(input, output, size);
                });
            }
            if (!ready) {
                control?.ThrowIfCanceled();
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
