using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Modpack;

public sealed class PackChange {
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Before { get; set; }
    public JObject? File { get; set; }
    public bool IsMod => PackProtocol.ManagedMod(Path);
    internal string? DependsOn { get; set; }
    internal string? ToggleDestination => Kind switch {
        "禁用" => Path + ".disabled",
        "启用" => PackProtocol.EnabledModPath(Path),
        _ => null
    };
}
public sealed class PackPlan {
    public JObject Release { get; set; } = new();
    public JObject State { get; set; } = new();
    public List<PackChange> Changes { get; } = new();
    public HashSet<string> Selected { get; set; } = new();
    public List<string> Preserved { get; } = new();
    public int? BaselineMatches { get; internal set; }
    internal List<JObject> RetainedRecords { get; } = new();
    internal Dictionary<string, List<JObject>> PriorForOptional { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> FileTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string[]> ModProjectsByHash { get; set; } = new(StringComparer.Ordinal);
    public bool IsApplied => (string?)Release["schema"] == PackProtocol.ArchiveRelease && (long?)State["sequence"] == (long?)Release["release"]?["sequence"];
    public long Bytes => Changes.Where(c => c.File != null).Sum(c => (long)c.File!["size"]!);

    public IEnumerable<(PackChange Change, string[] PreviousPaths)> PreviewChanges() {
        var replacements = new HashSet<string>(
            Changes.Where(c => c.File != null && c.IsMod).Select(c => c.Path),
            StringComparer.OrdinalIgnoreCase);
        var oldVersions = Changes.Where(c => c.Kind == "删除旧版本" && c.DependsOn != null && replacements.Contains(c.DependsOn))
            .ToLookup(c => c.DependsOn!, StringComparer.OrdinalIgnoreCase);
        foreach (var change in Changes) {
            if (change.DependsOn != null && oldVersions[change.DependsOn].Contains(change)) continue;
            yield return (change, oldVersions[change.Path].Select(c => c.Path).ToArray());
        }
    }
}
public sealed class PackRuntime {
    public List<string> Paths { get; } = new();
    public Action Commit { get; set; } = () => { };
    public Action Verify { get; set; } = () => { };
}

/// <summary>One instance, one transaction, one retained rollback. Caller holds the PCL instance lock.</summary>
public sealed partial class PackUpdater {
    private readonly string root;
    private readonly Action<IEnumerable<PackPublishFile>>? resolveModSources;
    private string Work(string relative) => PackProtocol.Safe(root, ".pcl-update/" + relative, true);
    public PackUpdater(string root, Action<IEnumerable<PackPublishFile>>? resolveModSources = null) {
        this.root = System.IO.Path.GetFullPath(root);
        this.resolveModSources = resolveModSources;
    }
    public bool NeedsRecovery => File.Exists(Work("pending/journal.json"));
    public bool HasBackup => File.Exists(Work("previous/journal.json"));
    public JObject State => File.Exists(Work("state.json")) ? PackProtocol.Parse(File.ReadAllBytes(Work("state.json"))) : new JObject();
    public JObject Binding => File.Exists(Work("binding.json")) ? PackProtocol.Parse(File.ReadAllBytes(Work("binding.json"))) : new JObject();
    public string ResolveIndexAddress(string source) {
        var binding = Binding;
        return string.Equals(source, (string?)binding["source"], StringComparison.Ordinal)
            ? (string?)binding["index_url"] ?? source
            : PackProtocol.IndexAddress(source);
    }
    public void Bind(string source, string pack, string channel, PackMirror? mirror = null) {
        PackProtocol.Https(source);
        if (State["pack_id"] != null && ((string?)State["pack_id"] != pack || (string?)State["channel"] != channel)) throw new InvalidOperationException("此实例已有其他整合包或频道的更新记录，请使用独立实例。");
        var binding = new JObject { ["source"] = source, ["index_url"] = ResolveIndexAddress(source), ["pack_id"] = pack, ["channel"] = channel };
        binding["mirror_url"] = mirror?.Address ?? "";
        if (mirror != null) binding["prefer_mirror"] = mirror.PreferMirror;
        Atomic(Work("binding.json"), binding);
    }
    public static JObject GetIndex(string source, PackMirror? mirror = null) {
        JObject Read(string address, bool _) {
            var value = PackProtocol.Parse(PackProtocol.Fetch(address, 8 * 1024 * 1024, mirror != null));
            PackProtocol.Validate(value, "index");
            return value;
        }
        return mirror == null ? Read(source, false) : mirror.Read(source, PackProtocol.IndexPath, Read);
    }
    public static JObject GetRelease(JObject pack, JObject channel, PackMirror? mirror = null) {
        JObject Read(string address, bool _) {
            var bytes = PackProtocol.Fetch(address, 8 * 1024 * 1024, mirror != null);
            if (PackProtocol.Hash(bytes) != (string?)channel["manifest_sha256"]) throw new InvalidDataException("更新清单校验失败，请重新检查更新。");
            var value = PackProtocol.Parse(bytes); PackProtocol.Validate(value, "release");
            if ((string?)value["pack"]!["id"] != (string?)pack["id"] || (string?)value["release"]!["channel"] != (string?)channel["id"] || (string?)value["release"]!["id"] != (string?)channel["release_id"] || (long)value["release"]!["sequence"]! != (long)channel["sequence"]!) throw new InvalidDataException("服务器目录和版本清单不一致。");
            return value;
        }
        string primary = (string)channel["manifest_url"]!;
        string key = PackPublisher.ManifestKey((string)pack["id"]!, (string)channel["id"]!, (string)channel["release_id"]!);
        return mirror == null ? Read(primary, false) : mirror.Read(primary, key, Read);
    }
    private string? Hash(string relative, bool internalFile = false) { string path = PackProtocol.Safe(root, relative, internalFile); if (Directory.Exists(path)) throw new IOException("目标路径被文件夹占用：" + relative); return File.Exists(path) ? PackProtocol.HashFile(path) : null; }
    private static bool Included(JToken item, HashSet<string> selected) => item["component"] == null || item["component"]!.Type == JTokenType.Null || selected.Contains((string)item["component"]!);
    public PackPlan Plan(JObject release, IEnumerable<string> selected) => Plan(release, selected, new Dictionary<string, string[]>(StringComparer.Ordinal));

    private PackPlan Plan(JObject release, IEnumerable<string> selected, Dictionary<string, string[]> modProjects) {
        if (NeedsRecovery) throw new IOException("上次更新尚未完成，请先恢复。");
        PackProtocol.Validate(release, "release");
        var state = State;
        if (state["pack_id"] != null) {
            if ((string?)state["pack_id"] != (string?)release["pack"]!["id"] || (string?)state["channel"] != (string?)release["release"]!["channel"]) throw new IOException("实例绑定的整合包或频道不匹配。");
            long sequence = (long)release["release"]!["sequence"]!;
            if (sequence < (long)state["sequence"]! || (sequence == (long)state["sequence"]! && (string?)state["release_id"] != (string?)release["release"]!["id"])) throw new IOException("服务器返回了旧版本或冲突的发布序号。");
            if (sequence == (long)state["sequence"]! && (string?)state["manifest_hash"] != PackProtocol.Hash(Encoding.UTF8.GetBytes(release.ToString(Formatting.None)))) throw new IOException("已发布的更新清单被修改，请发布新版本。");
        }
        var choice = new HashSet<string>(selected);
        foreach (var c in release["components"]!) if ((bool)c["required"]!) choice.Add((string)c["id"]!);
        var plan = new PackPlan { Release = (JObject)release.DeepClone(), State = state, Selected = choice, ModProjectsByHash = modProjects };
        if ((string?)release["schema"] == PackProtocol.ArchiveRelease) return PlanArchive(plan);
        var previous = (state["files"] as JArray ?? new JArray()).Cast<JObject>().ToDictionary(f => (string)f["path"]!, StringComparer.OrdinalIgnoreCase);
        foreach (JObject file in release["files"]!.Where(f => Included(f, choice))) {
            string path = (string)file["path"]!; string? hash = Hash(path);
            if (hash != null && previous.TryGetValue(path, out var old) && PartialConfig(plan, old)) {
                plan.Preserved.Add(path);
                plan.RetainedRecords.Add(old);
                continue;
            }
            if ((string?)file["write_policy"] == "preserve" && hash != null) continue;
            if (hash == (string?)file["sha256"]) continue;
            Add(plan, file, hash == null ? "新增" : "更新 / 修复", hash, file);
        }
        var targets = new HashSet<string>(release["files"]!.Select(f => (string)f["path"]!), StringComparer.OrdinalIgnoreCase);
        foreach (JObject action in release["actions"]!.Where(a => Included(a, choice))) {
            string path = (string)action["path"]!; string? hash = Hash(path);
            if (hash == null) continue;
            if (hash != (string?)action["expected_sha256"]) throw new IOException("此文件已被修改，请先移走或备份后重试：" + path);
            if ((string?)action["action"] == "disable") {
                string? disabled = Hash(path + ".disabled");
                if (disabled != null && disabled != hash) throw new IOException("禁用目标已存在且内容不同：" + path + ".disabled");
                Add(plan, action, "禁用", hash, null);
            } else Add(plan, action, "删除", hash, null);
        }
        var cleanupChoice = CleanupChoice(plan);
        foreach (JObject old in (state["files"] as JArray ?? new JArray()).Where(f => Included(f, cleanupChoice))) {
            string path = (string)old["path"]!;
            if (targets.Contains(path) || plan.Changes.Any(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            string? hash = Hash(path); if (hash == null) continue;
            if (PartialConfig(plan, old)) { plan.Preserved.Add(path); plan.RetainedRecords.Add(old); continue; }
            if (hash != (string?)old["sha256"]) throw new IOException("旧文件有本地修改，自动清理已停止：" + path);
            Add(plan, old, "删除", hash, null);
        }
        return plan;
    }
    private static void Add(PackPlan plan, JObject item, string kind, string? hash, JObject? file) => plan.Changes.Add(new PackChange {
        Path = (string)item["path"]!, Name = (string?)item["display"]?["name"] ?? (string)item["path"]!, Icon = (string?)item["display"]?["icon_url"] ?? "",
        Kind = kind, Before = hash, File = file, Reason = (string?)item["reason"] ?? ""
    });
    public void Apply(PackPlan preview, Func<PackRuntime> prepareRuntime, Action<string> progress, Action<string, string, long>? download = null,
        PackDownloadOptions? options = null) {
        if (NeedsRecovery) throw new IOException("请先恢复上次更新。");
        var plan = Plan(preview.Release, preview.Selected, preview.ModProjectsByHash);
        if (Signature(plan) != Signature(preview)) throw new IOException("本地文件已变化，请重新检查更新。");
        if (plan.IsApplied) { progress("此版本已应用。"); return; }
        bool archived = (string?)plan.Release["schema"] == PackProtocol.ArchiveRelease;
        var stage = Work("stage"); Clear(stage); Directory.CreateDirectory(stage);
        try {
            var staged = new Dictionary<PackChange, string>();
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            using (var archive = archived ? new PackArchive((JObject)plan.Release["archive"]!, Work("downloads/" + (string)plan.Release["archive"]!["sha256"]! + ".zip"),
                progress, options?.ApproveWholeArchive, options?.DownloadRange, options?.Mirror) : null)
            foreach (var change in plan.Changes.Where(c => c.File != null)) {
                if (archived) {
                    if (!StageArchiveFile(change, archive!, staged, progress, download, options)) skipped.Add(change.Path);
                    continue;
                }
                var file = change.File!; string target = Work("stage/" + index++); Exception? error = null;
                foreach (var address in file["urls"]!) {
                    string url = address.Value<string>()!;
                    try {
                        progress("正在下载：" + change.Name);
                        if (download != null) download(url, target, (long)file["size"]!);
                        else { using var output = File.Create(target); PackProtocol.Download(url, output, (long)file["size"]!, bytes => progress($"{change.Name}：{bytes / 1024:N0} / {(long)file["size"]! / 1024:N0} KB")); }
                        if (new FileInfo(target).Length != (long)file["size"]! || PackProtocol.HashFile(target) != (string?)file["sha256"]) throw new IOException("下载文件校验失败：" + change.Path);
                        error = null; break;
                    } catch (Exception ex) { error = ex; }
                }
                if (error != null) throw error;
                staged[change] = target;
            }
            progress("正在准备游戏运行环境……"); var runtime = prepareRuntime();
            if (plan.Changes.Count == 0 && runtime.Paths.Count == 0 && (string?)plan.State["release_id"] == (string?)plan.Release["release"]!["id"] &&
                new HashSet<string>((plan.State["selected"] as JArray ?? new JArray()).Select(item => item.Value<string>()!)).SetEquals(plan.Selected)) {
                progress("已是最新，文件检查通过。"); return;
            }
            if (Signature(Plan(preview.Release, preview.Selected, plan.ModProjectsByHash)) != Signature(plan)) throw new IOException("下载期间本地文件发生变化，请重新检查。");
            var changes = plan.Changes.Where(c => !skipped.Contains(c.Path) &&
                (c.DependsOn == null || !skipped.Contains(c.DependsOn))).ToList();
            var paths = changes.Select(c => c.Path).Concat(changes.Select(c => c.ToggleDestination).OfType<string>()).Concat(runtime.Paths).Append(".pcl-update/state.json").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Any(p => p.StartsWith(".pcl-update/", StringComparison.OrdinalIgnoreCase) && p != ".pcl-update/state.json")) throw new IOException("运行环境请求修改更新器内部文件。");
            string pending = Work("pending");
            Clear(pending); // Older builds could leave an unpublished backup here.
            string backupFolder = Work("stage/backup");
            Directory.CreateDirectory(backupFolder);
            var entries = new JArray(); int n = 0;
            foreach (string path in paths) {
                string source = PackProtocol.Safe(root, path, true); bool exists = File.Exists(source);
                string backup = "files/" + n++;
                if (exists) { string dest = PackProtocol.Safe(backupFolder, backup, true); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!); File.Copy(source, dest); }
                entries.Add(new JObject { ["path"] = path, ["backup"] = backup, ["existed"] = exists });
            }
            var journal = new JObject { ["status"] = "applying", ["files"] = entries };
            Atomic(Work("stage/backup/journal.json"), journal);
            Directory.Move(backupFolder, pending);
            try {
                progress("正在应用运行环境……"); runtime.Commit();
                foreach (var change in changes) {
                    string target = PackProtocol.Safe(root, change.Path);
                    progress(change.Kind + "：" + change.Name);
                    if (change.File != null) {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                        File.Copy(staged[change], target, true);
                        if (Hash(change.Path) != PackProtocol.HashFile(staged[change])) throw new IOException("更新后校验失败：" + change.Path);
                    }
                    else if (change.ToggleDestination is string destination) {
                        string fullPath = PackProtocol.Safe(root, destination);
                        if (!File.Exists(fullPath)) File.Move(target, fullPath);
                        else if (Hash(destination) == change.Before) File.Delete(target);
                        else throw new IOException("Mod 启停状态切换目标已改变：" + destination);
                    }
                    else File.Delete(target);
                }
                runtime.Verify();
                var retainedConfigs = new HashSet<string>(plan.RetainedRecords.Select(f => (string)f["path"]!), StringComparer.OrdinalIgnoreCase);
                if (!archived) foreach (var file in plan.Release["files"]!.Where(f => Included(f, plan.Selected) && (string?)f["write_policy"] == "replace" && !retainedConfigs.Contains((string)f["path"]!)))
                    if (Hash((string)file["path"]!) != (string?)file["sha256"]) throw new IOException("更新后校验失败：" + (string)file["path"]!);
                var owned = new JArray();
                var allTargets = new HashSet<string>(plan.Release["files"]!.Where(f => Included(f, plan.Selected)).Select(f => (string)f["path"]!), StringComparer.OrdinalIgnoreCase);
                foreach (JObject old in (plan.State["files"] as JArray ?? new JArray()).Where(f => !Included(f, CleanupChoice(plan)))) if (!allTargets.Contains((string)old["path"]!)) owned.Add(old.DeepClone());
                if (archived) RecordArchiveOwnership(plan, owned, skipped);
                else {
                    foreach (var config in plan.RetainedRecords) owned.Add(config.DeepClone());
                    foreach (JObject file in plan.Release["files"]!.Where(f => Included(f, plan.Selected) && (string?)f["write_policy"] == "replace" && !retainedConfigs.Contains((string)f["path"]!)))
                        owned.Add(new JObject { ["path"] = file["path"]!.DeepClone(), ["sha256"] = file["sha256"]!.DeepClone(), ["whole_file"] = true, ["component"] = file["component"]?.DeepClone() });
                }
                Atomic(Work("state.json"), new JObject { ["pack_id"] = plan.Release["pack"]!["id"]!.DeepClone(), ["channel"] = plan.Release["release"]!["channel"]!.DeepClone(), ["release_id"] = plan.Release["release"]!["id"]!.DeepClone(), ["sequence"] = plan.Release["release"]!["sequence"]!.DeepClone(), ["runtime"] = plan.Release["runtime"]!.DeepClone(), ["selected"] = new JArray(plan.Selected), ["files"] = owned, ["manifest_hash"] = PackProtocol.Hash(Encoding.UTF8.GetBytes(plan.Release.ToString(Formatting.None))) });
                foreach (JObject entry in entries) entry["after"] = Hash((string)entry["path"]!, true);
                journal["status"] = "committed"; Atomic(Work("pending/journal.json"), journal);
            } catch { Restore("pending", false); throw; }
            Finish();
            if (archived) {
                try { Clear(Work("downloads")); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            progress("更新完成，可以启动游戏。已保留最近一次回退备份。");
        } finally { Clear(stage); }
    }
    private static string Signature(PackPlan plan) => string.Join("\n", plan.Changes.Select(c => c.Path + "|" + c.Kind + "|" + c.Before + "|" + c.DependsOn))
        + string.Join("\n", plan.FileTargets.Select(pair => pair.Key + "|" + pair.Value))
        + string.Join("\n", plan.RetainedRecords.Select(item => (string)item["path"]!)) + plan.State.ToString(Formatting.None);
    private static HashSet<string> CleanupChoice(PackPlan plan) {
        var result = new HashSet<string>(plan.Selected);
        var defined = new HashSet<string>(plan.Release["components"]!.Select(c => (string)c["id"]!));
        foreach (var old in plan.State["selected"] as JArray ?? new JArray()) if (!defined.Contains((string)old!)) result.Add((string)old!);
        return result;
    }
    public void Recover() {
        if (!NeedsRecovery) return;
        var journal = PackProtocol.Parse(File.ReadAllBytes(Work("pending/journal.json")));
        if ((string?)journal["status"] == "committed") Finish(); else Restore("pending", false);
    }
    public void Rollback() {
        if (NeedsRecovery) { Recover(); return; }
        Clear(Work("pending")); // Discard an incomplete pre-journal backup from an older build.
        var journal = PackProtocol.Parse(File.ReadAllBytes(Work("previous/journal.json")));
        foreach (var entry in journal["files"]!) if (Hash((string)entry["path"]!, true) != (string?)entry["after"]) throw new IOException("更新后文件发生变化，无法自动回退：" + (string)entry["path"]!);
        journal["status"] = "restoring";
        Atomic(Work("previous/journal.json"), journal);
        Directory.Move(Work("previous"), Work("pending"));
        Restore("pending", false);
    }
    private void Restore(string folder, bool guard) {
        var journal = PackProtocol.Parse(File.ReadAllBytes(Work(folder + "/journal.json")));
        var entries = ((JArray)journal["files"]!).Cast<JObject>().ToList();
        if (guard) foreach (var entry in entries) if (Hash((string)entry["path"]!, true) != (string?)entry["after"]) throw new IOException("更新后文件发生变化，无法自动回退：" + (string)entry["path"]!);
        foreach (var entry in entries.AsEnumerable().Reverse()) {
            string target = PackProtocol.Safe(root, (string)entry["path"]!, true);
            if ((bool)entry["existed"]!) { string backup = PackProtocol.Safe(Work(folder), (string)entry["backup"]!, true); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); File.Copy(backup, target, true); }
            else if (File.Exists(target)) File.Delete(target);
        }
        Clear(Work(folder));
    }
    private void Finish() { Clear(Work("previous")); Directory.Move(Work("pending"), Work("previous")); }
    internal static void Atomic(string path, JObject value) {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); string temporary = path + ".tmp";
        File.WriteAllText(temporary, value.ToString(Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }
    private void Clear(string path) {
        // Delete only fixed transaction directories, and never follow reparse points.
        if (!new[] { Work("stage"), Work("pending"), Work("previous"), Work("downloads") }.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new IOException("无效的清理目录。");
        if (!Directory.Exists(path)) return;
        DeleteTree(path);
    }
    private static void DeleteTree(string path) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新目录不能是链接。");
        foreach (string file in Directory.GetFiles(path)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份中发现链接。"); File.Delete(file); }
        foreach (string dir in Directory.GetDirectories(path)) DeleteTree(dir);
        Directory.Delete(path);
    }
}
