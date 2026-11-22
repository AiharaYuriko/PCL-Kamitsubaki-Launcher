using System.IO;
using System.Text.RegularExpressions;
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
    public JArray? ModSources { get; internal set; }
    public bool IsMod => PackProtocol.ManagedMod(Path);
    internal const string ArchiveDownloadSource = "整合包 ZIP";
    public IEnumerable<string> DownloadSources {
        get {
            if (File == null) return Enumerable.Empty<string>();
            var sources = (File["urls"] as JArray ?? new JArray()).Values<string>()
                .Select(url => DownloadSourceFor(url!));
            if (File["entry"] is JObject) sources = sources.Append(ArchiveDownloadSource);
            return sources.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
    internal static string DownloadSourceFor(string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || string.IsNullOrEmpty(address.Host))
            return "直接下载";
        string host = address.IdnHost.TrimEnd('.');
        static bool Matches(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        if (Matches(host, "modrinth.com")) return "Modrinth";
        if (Matches(host, "forgecdn.net") || Matches(host, "curseforge.com")) return "CurseForge";
        return host;
    }
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
    public bool IsCurrentArchiveRelease => (string?)Release["schema"] == PackProtocol.ArchiveRelease &&
        (long?)State["sequence"] == (long?)Release["release"]?["sequence"];
    public bool IsApplied => IsCurrentArchiveRelease && Changes.Count == 0;
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
    public JObject? CheckForUpdate() {
        var state = State;
        var installedId = (string?)state["release_id"];
        if (string.IsNullOrEmpty(installedId)) return null;
        if (NeedsRecovery) throw new IOException("上次更新尚未完成，请先恢复。");

        var binding = Binding;
        var source = (string?)binding["source"];
        var packId = (string?)state["pack_id"];
        var channelId = (string?)state["channel"];
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrEmpty(packId) || string.IsNullOrEmpty(channelId) ||
            packId != (string?)binding["pack_id"] || channelId != (string?)binding["channel"])
            throw new IOException("此实例缺少匹配的整合包更新源。");

        var mirror = GetBoundMirror(binding);
        var index = GetIndex((string?)binding["index_url"] ?? source!, mirror, shortTimeout: true);
        var pack = index["packs"]!.OfType<JObject>().FirstOrDefault(item => (string?)item["id"] == packId);
        var channel = pack?["channels"]!.OfType<JObject>().FirstOrDefault(item => (string?)item["id"] == channelId);
        if (pack == null || channel == null) throw new IOException("更新源没有此整合包或频道。");

        long installedSequence = (long)state["sequence"]!;
        long latestSequence = (long)channel["sequence"]!;
        if (latestSequence < installedSequence || (latestSequence == installedSequence && (string?)channel["release_id"] != installedId))
            throw new IOException("服务器返回了旧版本或冲突的发布序号。");
        if (latestSequence == installedSequence) return null;

        var release = GetRelease(pack, channel, mirror, shortTimeout: true);
        if (NeedsRecovery || !JToken.DeepEquals(state, State) || !JToken.DeepEquals(binding, Binding)) return null;
        return release;
    }

    public JObject? CheckLegacyStartupUpdate(string bundleRoot) {
        if (State.HasValues || NeedsRecovery || !IsBundledLegacyInstance(bundleRoot)) return null;

        const string packId = "kvc";
        const string channelId = "stable";
        var binding = Binding;
        string source = PackMirror.DefaultPrimaryAddress;
        PackMirror? mirror = new PackMirror(PackMirror.DefaultAddress, preferMirror: true);
        if (binding.HasValues) {
            source = (string?)binding["source"] ?? "";
            if (PackMirror.DefaultAddressFor(source).Length == 0 ||
                (string?)binding["pack_id"] != packId || (string?)binding["channel"] != channelId) return null;
            mirror = GetBoundMirror(binding);
        }

        var index = GetIndex((string?)binding["index_url"] ?? PackProtocol.IndexAddress(source), mirror, shortTimeout: true);
        var pack = index["packs"]!.OfType<JObject>().FirstOrDefault(item => (string?)item["id"] == packId);
        var channel = pack?["channels"]!.OfType<JObject>().FirstOrDefault(item => (string?)item["id"] == channelId);
        if (pack == null || channel == null) throw new IOException("更新源没有神椿市搬砖中的稳定频道。");

        var release = GetRelease(pack, channel, mirror, shortTimeout: true);
        return State.HasValues || NeedsRecovery || !JToken.DeepEquals(binding, Binding) ? null : release;
    }

    internal bool IsBundledLegacyInstance(string bundleRoot) {
        var versions = Path.Combine(bundleRoot, ".minecraft", "versions");
        var primary = Path.Combine(versions, "神椿市搬砖中");
        var legacy = Path.Combine(versions, "1.21.1-NeoForge_21.1.219");
        if (!Directory.Exists(Path.Combine(root, "mods"))) return false;

        var expected = Directory.Exists(Path.Combine(primary, "mods")) ? primary : legacy;
        if (!string.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                           Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) return false;

        const string legacyManifest = "https://r2.iris-mapper.top/minecraft/releases/current/release-manifest.json";
        var config = Path.Combine(bundleRoot, "client-config.toml");
        return File.Exists(config) && File.ReadLines(config).Any(line => Regex.IsMatch(line,
            @"^[ \t]*manifest_url[ \t]*=[ \t]*(['""])" + Regex.Escape(legacyManifest) + @"\1[ \t]*(?:#.*)?$"));
    }
    public static PackMirror? GetBoundMirror(JObject binding) {
        var address = (string?)binding["mirror_url"] ?? PackMirror.DefaultAddressFor((string?)binding["source"] ?? "");
        return string.IsNullOrWhiteSpace(address) ? null : new PackMirror(address, (bool?)binding["prefer_mirror"] ?? true);
    }
    public static JObject GetIndex(string source, PackMirror? mirror = null) => GetIndex(source, mirror, shortTimeout: false);
    private static JObject GetIndex(string source, PackMirror? mirror, bool shortTimeout) {
        JObject Read(string address, bool _) {
            var value = PackProtocol.Parse(PackProtocol.Fetch(address, 8 * 1024 * 1024, shortTimeout || mirror != null));
            PackProtocol.Validate(value, "index");
            return value;
        }
        return mirror == null ? Read(source, false) : mirror.Read(source, PackProtocol.IndexPath, Read);
    }
    public static JObject GetRelease(JObject pack, JObject channel, PackMirror? mirror = null) => GetRelease(pack, channel, mirror, shortTimeout: false);
    private static JObject GetRelease(JObject pack, JObject channel, PackMirror? mirror, bool shortTimeout) {
        JObject Read(string address, bool _) {
            var bytes = PackProtocol.Fetch(address, 8 * 1024 * 1024, shortTimeout || mirror != null);
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
        Kind = kind, Before = hash, File = file, ModSources = item["sources"] as JArray, Reason = (string?)item["reason"] ?? ""
    });
    internal const int DownloadRounds = 2;

    public void Apply(PackPlan preview, Func<PackRuntime> prepareRuntime, Action<string> progress, Action<string, string, long>? download = null,
        PackDownloadOptions? options = null) {
        var control = options?.Control;
        control?.ThrowIfCanceled();
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
            var downloads = plan.Changes.Where(c => c.File != null).ToList();
            int fileNumber = 0;
            var reportTransfer = options?.ReportTransfer;
            void ReportTransfer(PackTransferProgress snapshot) {
                string text = $"({fileNumber}/{downloads.Count}) {snapshot.Text}";
                if (reportTransfer == null) progress(text);
                else reportTransfer(snapshot with { Text = text });
            }
            using (var archive = archived ? new PackArchive((JObject)plan.Release["archive"]!, Work("downloads/" + (string)plan.Release["archive"]!["sha256"]! + ".zip"),
                progress, options?.ApproveWholeArchive, options?.DownloadRange, options?.Mirror, ReportTransfer, control) : null)
            foreach (var change in downloads) {
                control?.ThrowIfCanceled();
                fileNumber++;
                if (archived) {
                    if (!StageArchiveFile(change, archive!, staged, progress, download, options, ReportTransfer)) skipped.Add(change.Path);
                    continue;
                }
                var file = change.File!; string target = Work("stage/" + index++); Exception? error = null;
                var urls = file["urls"]!.Values<string>().ToArray();
                for (int round = 0; round < DownloadRounds; round++) {
                    string title = round == 0 ? change.Name : $"{change.Name}（第 {round + 1} 轮）";
                    for (int route = 0; route < urls.Length; route++) {
                        control?.ThrowIfCanceled();
                        string url = urls[route]!;
                        try {
                            string label = "正在从 " + PackChange.DownloadSourceFor(url) + " 下载：" + title;
                            progress(label);
                            if (download != null) download(url, target, (long)file["size"]!);
                            else {
                                using var output = File.Create(target);
                                bool canSwitch = route + 1 < urls.Length || (urls.Length > 1 && round + 1 < DownloadRounds);
                                PackProtocol.Download(url, output, (long)file["size"]!, label, ReportTransfer, control: control, canSwitch: canSwitch);
                            }
                            if (new FileInfo(target).Length != (long)file["size"]! || PackProtocol.HashFile(target) != (string?)file["sha256"]) throw new IOException("下载文件校验失败：" + change.Path);
                            error = null; break;
                        } catch (Exception ex) when (ex is not PackUpdateCanceledException) { error = ex; }
                    }
                    if (error == null) break;
                }
                if (error != null) throw error;
                staged[change] = target;
            }
            control?.ThrowIfCanceled();
            var changes = plan.Changes.Where(c => !skipped.Contains(c.Path) &&
                (c.DependsOn == null || !skipped.Contains(c.DependsOn))).ToList();
            if (plan.IsCurrentArchiveRelease && changes.Count == 0) { progress("没有可应用的模组修复。"); return; }
            progress("正在准备游戏运行环境……"); var runtime = prepareRuntime();
            control?.ThrowIfCanceled();
            if (plan.Changes.Count == 0 && runtime.Paths.Count == 0 && (string?)plan.State["release_id"] == (string?)plan.Release["release"]!["id"] &&
                new HashSet<string>((plan.State["selected"] as JArray ?? new JArray()).Select(item => item.Value<string>()!)).SetEquals(plan.Selected)) {
                progress("已是最新，文件检查通过。"); return;
            }
            if (Signature(Plan(preview.Release, preview.Selected, plan.ModProjectsByHash)) != Signature(plan)) throw new IOException("下载期间本地文件发生变化，请重新检查。");
            var paths = changes.Select(c => c.Path).Concat(changes.Select(c => c.ToggleDestination).OfType<string>()).Concat(runtime.Paths).Append(".pcl-update/state.json").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Any(p => p.StartsWith(".pcl-update/", StringComparison.OrdinalIgnoreCase) && p != ".pcl-update/state.json")) throw new IOException("运行环境请求修改更新器内部文件。");
            string pending = Work("pending");
            Clear(pending); // Older builds could leave an unpublished backup here.
            string backupFolder = Work("stage/backup");
            Directory.CreateDirectory(backupFolder);
            var entries = new JArray(); int n = 0;
            foreach (string path in paths) {
                control?.ThrowIfCanceled();
                string source = PackProtocol.Safe(root, path, true); bool exists = File.Exists(source);
                string backup = "files/" + n++;
                if (exists) { string dest = PackProtocol.Safe(backupFolder, backup, true); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!); File.Copy(source, dest); }
                entries.Add(new JObject { ["path"] = path, ["backup"] = backup, ["existed"] = exists });
            }
            var journal = new JObject { ["status"] = "applying", ["files"] = entries };
            Atomic(Work("stage/backup/journal.json"), journal);
            control?.BeginApply();
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
