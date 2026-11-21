using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace PCL.Modpack;

public sealed class PackPublishFile {
    public string Path { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? ModId { get; set; }
    internal bool FabricMod { get; set; }
    public long Size { get; set; }
    public string Category { get; set; } = "file";
    public string Change { get; set; } = "";
    public string? PreviousPath { get; set; }
    public string Operation { get; set; } = PackPublisher.Automatic;
    public string JsonKeys { get; set; } = "";
    public string Platform { get; set; } = "";
    public bool EmbedMod { get; set; }
    public bool Optional { get; set; }
    public JArray Sources { get; } = new();
    public List<string> Urls { get; } = new();
}

public sealed class PackPublishDetails {
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string ReleaseId { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Minecraft { get; set; } = "";
    public string NeoForge { get; set; } = "";
    public bool AllowDowngrade { get; set; }
    public string? BaselineId { get; set; }
}

public sealed record PackBaseline(string Id, string Name);

public sealed class PackPublisher(string root) {
    public const string Automatic = "自动";
    public const string Force = "强制固定 / 替换";
    public const string Delete = "强制删除";
    public const string Disable = "强制禁用";
    public const string Preserve = "仅在缺失时安装";
    public const string Patch = "修改 JSON 配置项";
    public const string Exclude = "排除";
    public static readonly string[] Operations = [Automatic, Force, Delete, Disable, Preserve, Patch, Exclude];
    public static readonly string[] DefaultFolders = ["mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks"];
    private static readonly DateTimeOffset ZipDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string LegacyBaselineId = "legacy";
    private const string LegacyBaselineName = "原有基线";
    private const string BaselinePreviewSources = "baseline-preview-sources.json";
    private readonly string? credentialDirectory;

    internal PackPublisher(string root, string credentialDirectory) : this(root) {
        this.credentialDirectory = credentialDirectory;
    }

    internal string ProjectFile(string name) => PackProtocol.Safe(root, ".pcl-update/publisher/" + name, true);
    public JObject Settings => Read("settings.json");
    public void SaveSettings(JObject settings) => PackUpdater.Atomic(ProjectFile("settings.json"), settings);
    public void SetBaselinePreviewSource(string baselineId, string source) {
        if (!Directory.Exists(source) || !Directory.Exists(Path.Combine(source, "mods")))
            throw new IOException("请选择包含 mods 文件夹的旧实例目录。");
        BaselinePath(baselineId);
        var sources = Read(BaselinePreviewSources);
        sources[baselineId] = Path.GetFullPath(source);
        PackUpdater.Atomic(ProjectFile(BaselinePreviewSources), sources);
    }
    public bool HasBaseline => Baselines.Count != 0;
    public JObject Latest => Read("latest.json");

    public IReadOnlyList<PackBaseline> Baselines => BaselinesFor(null);

    public IReadOnlyList<PackBaseline> BaselinesFor(string? scope) {
        var result = new List<PackBaseline>();
        if (File.Exists(ProjectFile("baseline.json"))) result.Add(new(LegacyBaselineId, LegacyBaselineName));
        string directory = ProjectFile("baselines");
        if (Directory.Exists(directory)) {
            var named = Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
                .Where(id => IsStoredBaselineId(id) && (!id.StartsWith("cloud-", StringComparison.Ordinal) ||
                    (scope != null && id.StartsWith("cloud-" + scope + "-", StringComparison.Ordinal))))
                .Select(id => new PackBaseline(id,
                    (string)Read(BaselinePath(id))["name"]! + (id.StartsWith("cloud-", StringComparison.Ordinal) ? "（云端）" : "")));
            result.AddRange(named.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        return result;
    }

    public static string? PublishedSource(JObject manifest) {
        string? archive = (string?)manifest["archive"]?["download_url"];
        string? hash = (string?)manifest["archive"]?["sha256"];
        if (archive == null || hash == null) return null;
        string suffix = "/" + ArchiveKey(hash);
        return archive.EndsWith(suffix, StringComparison.Ordinal) ? archive.Substring(0, archive.Length - suffix.Length) : null;
    }
    public static string? CredentialTarget(string account, string bucket, bool mirror = false) {
        if (mirror) return PackS3MirrorSettings.ValidBucket(bucket) ? $"s3/{PackS3MirrorSettings.Host}/{bucket}" : null;
        return PackR2Settings.ValidTarget(account, bucket) ? $"r2/{account}/{bucket}" : null;
    }

    private string CredentialPath(string target) {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (credentialDirectory == null && localAppData.Length == 0)
            throw new IOException("无法定位当前 Windows 用户的本地应用数据目录。");
        string directory = credentialDirectory ?? Path.Combine(localAppData, "PCL-Kamitsubaki", "publisher", "credentials");
        return Path.Combine(directory, PackProtocol.Hash(Encoding.UTF8.GetBytes(target)) + ".json");
    }

    private static T WithCredentialLock<T>(string path, Func<T> action) {
        string id = PackProtocol.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        using var mutex = new Mutex(false, @"Global\PCL-Kamitsubaki-publisher-" + id);
        try {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("等待发布凭据文件超时。");
        }
        catch (AbandonedMutexException) { }
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static JObject DecryptCredentials(JObject document, byte[]? entropy) {
        string encrypted = (string?)document["encrypted"] ?? throw new InvalidDataException("发布凭据文件格式无效。");
        return PackProtocol.Parse(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), entropy, DataProtectionScope.CurrentUser));
    }

    private static void WriteCredentials(string path, JObject value, byte[] entropy) {
        var encrypted = ProtectedData.Protect(PackR2Publisher.JsonBytes(value), entropy, DataProtectionScope.CurrentUser);
        PackUpdater.Atomic(path, new JObject { ["encrypted"] = Convert.ToBase64String(encrypted) });
    }

    public JObject ReadCredentials(string? target) {
        if (target == null) return new JObject();
        string path = CredentialPath(target);
        return WithCredentialLock(path, () => File.Exists(path)
            ? DecryptCredentials(PackProtocol.Parse(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes(target))
            : new JObject());
    }

    public void SaveCredentials(string? target, string access, string secret) {
        if (target == null) return;
        string path = CredentialPath(target);
        WithCredentialLock(path, () => {
            if (access.Length == 0 && secret.Length == 0) {
                DeleteCredentialFiles(path);
            } else {
                WriteCredentials(path, new JObject { ["access"] = access, ["secret"] = secret }, Encoding.UTF8.GetBytes(target));
            }
            return true;
        });
    }

    public void MigrateLegacyCredentials(string? target, bool mirror = false) {
        string legacy = ProjectFile(mirror ? "mirror-credentials.json" : "credentials.json");
        if (!File.Exists(legacy)) return;
        WithCredentialLock(legacy, () => {
            if (!File.Exists(legacy)) return false;
            var document = PackProtocol.Parse(File.ReadAllBytes(legacy));
            // Record the original binding before allowing instance settings to change.
            string? binding = (string?)document["target"];
            string id = target == null ? "" : Path.GetFileNameWithoutExtension(CredentialPath(target));
            if (binding == null) {
                document["target"] = binding = id;
                PackUpdater.Atomic(legacy, document);
            }
            if (target == null || binding != id) return false;

            string path = CredentialPath(target);
            return WithCredentialLock(path, () => {
                var entropy = Encoding.UTF8.GetBytes(target);
                var credentials = DecryptCredentials(document, null);
                if (File.Exists(path))
                    DecryptCredentials(PackProtocol.Parse(File.ReadAllBytes(path)), entropy);
                else
                    WriteCredentials(path, credentials, entropy);
                DeleteCredentialFiles(legacy);
                return true;
            });
        });
    }

    private static void DeleteCredentialFiles(string path) {
        try {
            File.Delete(path + ".tmp");
            File.Delete(path);
        }
        catch (DirectoryNotFoundException) { }
    }

    public string GetImportPath(string source) {
        string instance = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(source);
        if (!full.StartsWith(instance, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择当前实例内的文件：" + source);
        string relative = full.Substring(instance.Length).Replace('\\', '/');
        ResolveImportFile(relative);
        return relative;
    }

    private string ResolveImportFile(string path) {
        string full = ResolveScanPath(path);
        if (!File.Exists(full)) throw new IOException("找不到指定文件：" + path);
        if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && !PackProtocol.ManagedMod(path))
            throw new InvalidDataException("mods 目录只支持导入 .jar 或 .jar.disabled 文件：" + path);
        return full;
    }

    private string ResolveScanPath(string path) {
        string top = path.Split('/')[0];
        if (!DefaultFolders.Contains(top, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("路径不在可发布的整合包目录内：" + path);
        return PackProtocol.Safe(root, path);
    }

    private static IEnumerable<string> ScanFolders(IEnumerable<string> folders) => folders
        .Select(f => f.Trim().Replace('\\', '/').TrimEnd('/')).Where(f => f.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase);

    public List<PackPublishFile> Scan(IEnumerable<string> folders, bool compare = true, IEnumerable<string>? files = null,
        string? baselineId = null, Action<IEnumerable<PackPublishFile>>? resolveModSources = null) =>
        Scan(folders, compare, files, baselineId, includeMaps: true, resolveModSources);

    private List<PackPublishFile> Scan(IEnumerable<string> folders, bool compare, IEnumerable<string>? files, string? baselineId,
        bool includeMaps, Action<IEnumerable<PackPublishFile>>? resolveModSources = null) {
        var result = new Dictionary<string, PackPublishFile>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files ?? Enumerable.Empty<string>()) {
            string path = file.Replace('\\', '/');
            Add(path, ResolveImportFile(path));
        }
        foreach (string folder in ScanFolders(folders)) {
            string full = ResolveScanPath(folder);
            if (File.Exists(full)) Add(folder, full);
            else if (Directory.Exists(full)) Walk(full, folder, Add);
        }
        foreach (JObject map in includeMaps ? ReadMaps() : new JArray()) {
            string published = (string)map["path"]!;
            string source = ProjectFile("maps/" + (string)map["snapshot"]!);
            if ((bool?)map["file"] == true) Add(published, source);
            else Walk(source, published, Add);
        }
        resolveModSources?.Invoke(result.Values);
        if (compare) AddChanges(result, baselineId, resolveModSources);
        return result.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string path, string full) {
            PackProtocol.Safe(root, path);
            if (result.ContainsKey(path)) return;
            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && !PackProtocol.ManagedMod(path)) return;
            bool map = IsMapPath(path);
            var info = new FileInfo(full);
            var mod = PackProtocol.ManagedMod(path) ? ReadPrimaryMod(full) : null;
            result.Add(path, new PackPublishFile {
                Path = path, LocalPath = full, Sha256 = PackProtocol.HashFile(full), Size = info.Length,
                ModId = mod?.Id, FabricMod = mod?.Fabric == true,
                Category = map ? "map" : path.Split('/')[0].ToLowerInvariant() switch {
                    "mods" => "mod", "config" or "defaultconfigs" => "config", "kubejs" or "scripts" => "script",
                    "resourcepacks" => "resourcepack", "shaderpacks" => "shaderpack", _ => "file"
                },
                Operation = map ? Preserve : Automatic
            });
        }
    }

    public void SaveBaseline(IEnumerable<PackPublishFile> files) {
        StoreBaseline(LegacyBaselineId, LegacyBaselineName, InstanceName(root), VerifySnapshot(files));
    }

    public PackBaseline SaveBaseline(string name, IEnumerable<PackPublishFile> files) =>
        SaveNamedBaseline(name, InstanceName(root), VerifySnapshot(files));

    public PackBaseline ImportBaselineFolder(string name, string source, IEnumerable<string> folders, IEnumerable<string> paths,
        Action<IEnumerable<PackPublishFile>>? resolveModSources = null) {
        var old = new PackPublisher(source);
        var existingPaths = paths.Select(p => p.Replace('\\', '/')).Where(p => File.Exists(old.ResolveScanPath(p)));
        var files = old.Scan(folders, compare: false, files: existingPaths, baselineId: null, includeMaps: false, resolveModSources);
        return SaveNamedBaseline(name, InstanceName(source), old.VerifySnapshot(files));
    }

    public PackBaseline ImportBaselineZip(string name, string source, IEnumerable<string> folders, IEnumerable<string> paths,
        Action<IEnumerable<PackPublishFile>>? resolveModSources = null) {
        using var stream = File.OpenRead(source);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        // Platform archives can omit the actual Mod bytes needed for the baseline hashes.
        if (zip.GetEntry("manifest.json") is { } curseForge) {
            using var reader = new StreamReader(curseForge.Open());
            if ((string?)JObject.Parse(reader.ReadToEnd())["manifestType"] == "minecraftModpack")
                throw new InvalidDataException("请先安装 CurseForge 整合包，再从已安装的旧实例目录导入基线。");
        }
        if (zip.GetEntry("modrinth.index.json") is { } modrinth) {
            using var reader = new StreamReader(modrinth.Open());
            if (JObject.Parse(reader.ReadToEnd())["files"] is JArray { Count: > 0 })
                throw new InvalidDataException("请先安装带平台下载项的整合包，再从已安装的旧实例目录导入基线。");
        }

        var entries = new List<(string Prefix, string Path, ZipArchiveEntry Entry)>();
        foreach (var entry in zip.Entries.Where(e => e.Name.Length != 0)) {
            string full = entry.FullName.Replace('\\', '/');
            string[] parts = full.Split('/');
            int top = Array.FindIndex(parts, 0, parts.Length - 1, p => DefaultFolders.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (top < 0) continue;
            PackProtocol.ValidateRelative(full, true);
            entries.Add((string.Join("/", parts.Take(top)), string.Join("/", parts.Skip(top)), entry));
        }
        var prefixes = entries.Select(e => e.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
        if (prefixes.Count == 0) throw new InvalidDataException("ZIP 中找不到实例的整合包文件。");
        string prefix = prefixes[0];
        if (prefix.Length != 0 && prefixes.Skip(1).Any(p => !p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ZIP 中包含多个实例，请选择只包含一个实例的 ZIP。");

        var selectedFolders = ScanFolders(folders).ToArray();
        var selectedPaths = new HashSet<string>(paths.Select(p => p.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        var snapshot = new List<PackPublishFile>();
        var modEntries = new List<(PackPublishFile File, ZipArchiveEntry Entry)>();
        foreach (var item in entries.Where(e => string.Equals(e.Prefix, prefix, StringComparison.OrdinalIgnoreCase))) {
            string path = item.Path;
            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && !PackProtocol.ManagedMod(path)) continue;
            if (!selectedPaths.Contains(path)) {
                bool inScope = selectedFolders.Any(f => path.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));
                if (!inScope || path.Split('/').Any(SkipScannedName)) continue;
            }
            using var input = item.Entry.Open();
            var file = new PackPublishFile { Path = path, Sha256 = PackProtocol.HashStream(input) };
            if (PackProtocol.ManagedMod(path)) {
                using var mod = item.Entry.Open();
                var identity = ReadPrimaryMod(mod);
                file.ModId = identity?.Id;
                file.FabricMod = identity?.Fabric == true;
                file.Category = "mod";
                file.Size = item.Entry.Length;
                if (resolveModSources != null) modEntries.Add((file, item.Entry));
            }
            snapshot.Add(file);
        }
        if (modEntries.Count != 0 && resolveModSources != null) ResolveZipModSources(modEntries, resolveModSources);
        string instance = prefix.Length == 0 || prefix.Equals("overrides", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(source) : prefix.Split('/').Last();
        return SaveNamedBaseline(name, instance, snapshot);
    }

    private static void ResolveZipModSources(List<(PackPublishFile File, ZipArchiveEntry Entry)> mods,
        Action<IEnumerable<PackPublishFile>> resolveModSources) {
        string temporary = Path.Combine(Path.GetTempPath(), "pcl-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try {
            const int batchSize = 80;
            for (int start = 0; start < mods.Count; start += batchSize) {
                var batch = mods.Skip(start).Take(batchSize).ToArray();
                for (int i = 0; i < batch.Length; i++) {
                    var (file, entry) = batch[i];
                    file.LocalPath = Path.Combine(temporary, i + ".jar");
                    entry.ExtractToFile(file.LocalPath);
                }
                resolveModSources(batch.Select(mod => mod.File));
                foreach (var mod in batch) File.Delete(mod.File.LocalPath);
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private IEnumerable<PackPublishFile> VerifySnapshot(IEnumerable<PackPublishFile> files) {
        var included = files.Where(f => f.LocalPath.Length > 0 && f.Category != "map" && f.Operation != Exclude).ToList();
        foreach (var file in included)
            if (!PackArchive.Matches(PackProtocol.Safe(root, file.Path), file.Size, file.Sha256))
                throw new IOException("扫描后文件发生变化，请重新扫描：" + file.Path);
        return included;
    }

    private static string InstanceName(string path) => Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
    private static bool IsStoredBaselineId(string id) => Regex.IsMatch(id, "\\A(?:[0-9a-f]{32}|cloud-[0-9a-f]{16}-[0-9a-f]{24})\\z");
    private static string BaselinePath(string id) => id == LegacyBaselineId ? "baseline.json" :
        IsStoredBaselineId(id) ? "baselines/" + id + ".json" : throw new InvalidDataException("无效的整合包基线。");

    internal static bool ValidBaselineName(string? name) =>
        name != null && name.Length >= 1 && name.Length <= 80 && name == name.Trim() && !name.Any(char.IsControl);
    private static string NamedBaselineId(string name) => PackProtocol.Hash(Encoding.UTF8.GetBytes(name.ToUpperInvariant())).Substring(0, 32);

    private PackBaseline SaveNamedBaseline(string name, string instance, IEnumerable<PackPublishFile> files) {
        name = name.Trim();
        if (!ValidBaselineName(name) || name == LegacyBaselineName)
            throw new InvalidDataException("请输入 1 至 80 个字符的基线名称，并避开保留名称「原有基线」。");
        string id = NamedBaselineId(name);
        StoreBaseline(id, name, instance, files);
        return new PackBaseline(id, name);
    }

    private void StoreBaseline(string id, string name, string instance, IEnumerable<PackPublishFile> files) {
        var snapshot = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (snapshot.Count == 0) throw new IOException("当前扫描范围内没有可保存的旧整合包文件。");
        var document = new JObject { ["name"] = name, ["instance"] = instance, ["files"] = new JArray(snapshot.Select(Snapshot)) };
        var projects = new JObject();
        foreach (var group in snapshot.Where(f => PackProtocol.ManagedMod(f.Path)).GroupBy(f => f.Sha256)) {
            var keys = group.SelectMany(f => PackProtocol.ModProjects(f.Sources)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            if (keys.Length != 0) projects[group.Key] = new JArray(keys);
        }
        if (projects.HasValues) document["mod_projects"] = projects;
        ValidateBaselineDocument(document);
        WriteBaseline(id, document);
    }

    internal static void ValidateBaselineDocument(JObject document) {
        if (document["name"]?.Type != JTokenType.String || !ValidBaselineName((string?)document["name"]) ||
            document["instance"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)document["instance"]) ||
            document["files"] is not JArray { Count: > 0 } snapshot)
            throw new InvalidDataException("整合包基线清单格式无效。");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot) {
            if (entry is not JObject file || file["path"]?.Type != JTokenType.String || file["sha256"]?.Type != JTokenType.String ||
                !Regex.IsMatch((string)file["sha256"]!, "\\A[0-9a-f]{64}\\z") ||
                (file["id"] != null && (file["id"]!.Type != JTokenType.String || !Regex.IsMatch((string)file["id"]!, "\\A[a-z][a-z0-9_]{1,63}\\z"))) ||
                (file["fabric_id"] != null && (file["id"] != null || file["fabric_id"]!.Type != JTokenType.String ||
                    !Regex.IsMatch((string)file["fabric_id"]!, "\\A[a-z][a-z0-9_-]{1,63}\\z"))) ||
                file.Properties().Any(p => p.Name != "path" && p.Name != "sha256" && p.Name != "id" && p.Name != "fabric_id"))
                throw new InvalidDataException("基线包含无效的文件记录。");
            string path = (string)file["path"]!;
            PackProtocol.ValidateRelative(path, false);
            if (!unique.Add(PackProtocol.ManagedMod(path) ? PackProtocol.EnabledModPath(path) : path))
                throw new InvalidDataException("基线包含重复文件：" + path);
        }
        if (document["mod_projects"] != null) {
            var modHashes = new HashSet<string>(snapshot.Where(f => PackProtocol.ManagedMod((string)f["path"]!))
                .Select(f => (string)f["sha256"]!), StringComparer.Ordinal);
            if (document["mod_projects"] is not JObject projects || projects.Properties().Any(project =>
                !modHashes.Contains(project.Name) || project.Value is not JArray { Count: > 0 } keys ||
                keys.Any(key => key.Type != JTokenType.String || !Regex.IsMatch((string)key!, @"\A(?:modrinth|curseforge)/[a-zA-Z0-9_-]{1,128}\z"))))
                throw new InvalidDataException("基线 Mod 平台项目格式无效。");
        }
    }

    internal JObject BaselineDocument(string id) {
        var saved = Read(BaselinePath(id));
        if (saved["files"] == null) return saved;
        var document = new JObject { ["name"] = id == LegacyBaselineId ? new JValue(LegacyBaselineName) : saved["name"],
            ["instance"] = saved["instance"], ["files"] = saved["files"] };
        if (saved["mod_projects"] != null) document["mod_projects"] = saved["mod_projects"];
        return document;
    }

    internal void CacheCloudBaseline(JObject document, string scope, string hash) {
        ValidateBaselineDocument(document);
        string name = (string)document["name"]!;
        var local = BaselineDocument(name == LegacyBaselineName ? LegacyBaselineId : NamedBaselineId(name));
        if (JToken.DeepEquals(local, document)) return;
        string id = "cloud-" + scope + "-" + hash.Substring(0, 24);
        if (JToken.DeepEquals(BaselineDocument(id), document)) return;
        WriteBaseline(id, document);
    }

    private void WriteBaseline(string id, JObject value) {
        string destination = ProjectFile(BaselinePath(id));
        if (File.Exists(destination)) throw new IOException("已有同名基线：" + (string?)value["name"]);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try {
            File.WriteAllText(temporary, value.ToString(Formatting.Indented), new UTF8Encoding(false));
            File.Move(temporary, destination);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string ImportXaeroMap(string source, string revision) {
        string full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        string instance = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(instance, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(revision, "^[a-z0-9][a-z0-9_-]{0,39}$"))
            throw new InvalidDataException("请选择当前实例内的 Xaero 地图实例，并使用英文小写、数字、连字符或下划线填写地图版本。");
        string relative = full.Substring(instance.Length).Replace('\\', '/');
        string[] parts = relative.Split('/');
        int prefix = parts[0].Equals("XaeroWorldMap", StringComparison.OrdinalIgnoreCase) ? 1 :
            parts.Length > 1 && parts[0].Equals("xaero", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("world-map", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        if (prefix == 0 || parts.Length != prefix + 3 || !parts[prefix].StartsWith("Multiplayer_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 Xaero 世界地图的具体地图实例目录，位于多人服务器与维度目录下。");
        string key = string.Join("/", parts.Take(parts.Length - 1));
        string target = key + "/cm$pcl-" + revision;
        PackProtocol.Safe(root, target);
        var maps = ReadMaps();
        if (maps.Any(m => string.Equals((string?)m["path"], target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("此服务器和维度已导入同一地图版本。");
        string snapshot = Guid.NewGuid().ToString("N");
        string destination = ProjectFile("maps/" + snapshot);
        int count = 0;
        Walk(PackProtocol.Safe(root, relative), "", (entry, file) => {
            string output = PackProtocol.Safe(destination, entry, true);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output);
            count++;
        });
        if (count == 0) throw new InvalidDataException("所选地图目录没有可发布的文件。");
        var next = new JArray(maps.Where(m => !string.Equals((string?)m["key"], key, StringComparison.OrdinalIgnoreCase)).Select(m => m.DeepClone()));
        next.Add(new JObject { ["path"] = target, ["key"] = key, ["snapshot"] = snapshot });
        PackUpdater.Atomic(ProjectFile("maps.json"), new JObject { ["maps"] = next });
        return target;
    }

    public string ImportXaeroWaypoints(string source) {
        string full = Path.GetFullPath(source);
        string instance = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(instance, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择此实例内的 Xaero 航点文件。");
        string relative = full.Substring(instance.Length).Replace('\\', '/');
        string[] parts = relative.Split('/');
        int prefix = parts[0].Equals("XaeroWaypoints", StringComparison.OrdinalIgnoreCase) ? 1 :
            parts.Length > 1 && parts[0].Equals("xaero", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("minimap", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        string name = parts.Last();
        if (prefix == 0 || parts.Length < prefix + 3 || !parts[prefix].StartsWith("Multiplayer_", StringComparison.OrdinalIgnoreCase) ||
            !(name.Equals("waypoints.txt", StringComparison.OrdinalIgnoreCase) ||
              name.StartsWith("mw$", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("请选择服务器目录下的 Xaero 航点子世界文件。");
        var maps = ReadMaps();
        if (maps.Any(m => string.Equals((string?)m["path"], relative, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("该航点文件已导入。请在 Xaero 里另建一个公开航点子世界，再导入对应的文件。");
        string snapshot = Guid.NewGuid().ToString("N");
        string destination = ProjectFile("maps/" + snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(PackProtocol.Safe(root, relative), destination);
        maps.Add(new JObject { ["path"] = relative, ["snapshot"] = snapshot, ["file"] = true });
        PackUpdater.Atomic(ProjectFile("maps.json"), new JObject { ["maps"] = maps });
        return relative;
    }

    private static bool IsMapPath(string path) => path.StartsWith("XaeroWorldMap/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("xaero/world-map/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("xaero/minimap/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("XaeroWaypoints/", StringComparison.OrdinalIgnoreCase);
    private JArray ReadMaps() => Read("maps.json")["maps"] as JArray ?? new JArray();
    private JObject Read(string name) => File.Exists(ProjectFile(name)) ? PackProtocol.Parse(File.ReadAllBytes(ProjectFile(name))) : new JObject();
    private JObject ReadBaseline(string? id) {
        id ??= Baselines.FirstOrDefault()?.Id;
        return id == null ? new JObject() : Read(BaselinePath(id));
    }

    private void AddChanges(Dictionary<string, PackPublishFile> result, string? baselineId,
        Action<IEnumerable<PackPublishFile>>? resolveModSources) {
        var document = ReadBaseline(baselineId);
        if (document["files"] is not JArray baseline) {
            if (baselineId != null) throw new IOException("找不到所选基线，请重新选择。");
            foreach (var file in result.Values) file.Change = "基线候选";
            return;
        }
        var latest = Latest["files"] as JArray;
        var comparison = baselineId == null ? latest ?? baseline : baseline;
        string OldPath(JObject file) => (string)file["path"]!;
        var previous = comparison.Cast<JObject>().ToDictionary(OldPath, StringComparer.OrdinalIgnoreCase);
        var matched = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Pair(PackPublishFile file, JObject old) {
            matched.Add(file.Path, old);
            used.Add(OldPath(old));
        }

        var mods = result.Values.Where(f => PackProtocol.ManagedMod(f.Path)).ToArray();
        var oldMods = previous.Values.Where(f => PackProtocol.ManagedMod(OldPath(f))).ToArray();
        var oldById = oldMods.ToLookup(PackProtocol.ModIdentity, StringComparer.Ordinal);
        foreach (var group in mods.Where(f => f.ModId != null).GroupBy(f => (f.FabricMod ? "fabric/" : "forge/") + f.ModId)) {
            if (group.Count() == 1 && oldById[group.Key].Count() == 1) Pair(group.Single(), oldById[group.Key].Single());
        }

        // The prior bytes may live in a different instance. Reuse projects recorded for their exact hash.
        var knownProjects = (latest ?? new JArray()).Concat(comparison)
            .Select(f => (Hash: (string)f["sha256"]!, Projects: PackProtocol.ModProjects(f["sources"])))
            .Concat(mods.Select(f => (Hash: f.Sha256, Projects: PackProtocol.ModProjects(f.Sources))))
            .ToLookup(f => f.Hash, f => f.Projects, StringComparer.Ordinal);
        var baselineProjects = document["mod_projects"] as JObject;
        var availableOld = oldMods.Where(f => !used.Contains(OldPath(f))).ToArray();
        var unmatchedMods = mods.Where(f => !matched.ContainsKey(f.Path)).ToArray();
        var resolvedOld = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (baselineId != null && resolveModSources != null && unmatchedMods.Any(f => f.Sources.HasValues)) {
            string? source = (string?)Read(BaselinePreviewSources)[baselineId];
            if (source != null && Directory.Exists(source)) {
                var unresolved = availableOld.Where(f => {
                    string hash = (string)f["sha256"]!;
                    return baselineProjects?[hash] == null && !knownProjects[hash].Any(projects => projects.Any());
                });
                resolvedOld = ResolveOldProjects(source, unresolved, resolveModSources);
            }
        }
        var oldProjects = availableOld.SelectMany(f => {
            string hash = (string)f["sha256"]!;
            var projects = (baselineProjects?[hash]?.Values<string>() ?? Enumerable.Empty<string>())
                .Concat(knownProjects[hash].SelectMany(p => p))
                .Concat(resolvedOld.TryGetValue(hash, out var keys) ? keys : Array.Empty<string>()).Distinct(StringComparer.Ordinal);
            return projects.Select(project => (Project: project, File: f));
        }).ToLookup(match => match.Project, match => match.File, StringComparer.Ordinal);

        var projectPairs = unmatchedMods
            .SelectMany(f => PackProtocol.ModProjects(f.Sources).SelectMany(project => oldProjects[project])
                .Distinct().Select(old => (File: f, Old: old))).ToArray();
        var projectTargets = projectPairs.ToLookup(pair => pair.File.Path, StringComparer.OrdinalIgnoreCase);
        var projectOwners = projectPairs.ToLookup(pair => OldPath(pair.Old), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in projectPairs) {
            if (projectTargets[pair.File.Path].Count() == 1 && projectOwners[OldPath(pair.Old)].Count() == 1)
                Pair(pair.File, pair.Old);
        }

        foreach (var file in result.Values.Where(f => !matched.ContainsKey(f.Path))) {
            if (previous.TryGetValue(file.Path, out var old) && !used.Contains(OldPath(old))) Pair(file, old);
        }
        var oldByFileName = oldMods.Where(f => !used.Contains(OldPath(f)))
            .ToLookup(f => PackProtocol.EnabledModPath(OldPath(f)), StringComparer.OrdinalIgnoreCase);
        foreach (var group in mods.Where(f => !matched.ContainsKey(f.Path))
                     .GroupBy(f => PackProtocol.EnabledModPath(f.Path), StringComparer.OrdinalIgnoreCase)) {
            if (group.Count() == 1 && oldByFileName[group.Key].Count() == 1) Pair(group.Single(), oldByFileName[group.Key].Single());
        }

        foreach (var file in result.Values) {
            if (!matched.TryGetValue(file.Path, out var old)) {
                file.Change = "新增";
                continue;
            }
            bool samePath = file.Path.Equals(OldPath(old), StringComparison.OrdinalIgnoreCase);
            file.Change = samePath && file.Sha256 == (string?)old["sha256"] ? "相同" : "更新";
            if (!samePath) file.PreviousPath = OldPath(old);
        }
        foreach (var pair in previous) {
            if (used.Contains(pair.Key) || result.ContainsKey(pair.Key) || IsMapPath(pair.Key)) continue;
            result.Add(pair.Key, new PackPublishFile { Path = pair.Key, Sha256 = (string)pair.Value["sha256"]!, Change = "已移出" });
        }
    }

    private static Dictionary<string, string[]> ResolveOldProjects(string source, IEnumerable<JObject> records,
        Action<IEnumerable<PackPublishFile>> resolveModSources) {
        var files = new Dictionary<string, PackPublishFile>(StringComparer.Ordinal);
        foreach (var record in records) {
            string path = (string)record["path"]!;
            string hash = (string)record["sha256"]!;
            if (files.ContainsKey(hash)) continue;
            string enabled = PackProtocol.EnabledModPath(path);
            foreach (string candidate in new[] { path, path == enabled ? enabled + ".disabled" : enabled }) {
                string full = PackProtocol.Safe(source, candidate);
                var info = new FileInfo(full);
                if (!info.Exists || PackProtocol.HashFile(full) != hash) continue;
                files.Add(hash, new PackPublishFile { Path = candidate, LocalPath = full, Sha256 = hash, Size = info.Length, Category = "mod" });
                break;
            }
        }
        if (files.Count != 0) resolveModSources(files.Values);
        return files.Values.Where(file => file.Sources.HasValues && PackArchive.Matches(file.LocalPath, file.Size, file.Sha256))
            .ToDictionary(file => file.Sha256, file => PackProtocol.ModProjects(file.Sources).ToArray(), StringComparer.Ordinal);
    }

    private static JObject Snapshot(PackPublishFile file) {
        var entry = new JObject { ["path"] = file.Path, ["sha256"] = file.Sha256 };
        if (file.ModId != null) entry[file.FabricMod ? "fabric_id" : "id"] = file.ModId;
        return entry;
    }

    internal JObject Build(PackPublishDetails details, IReadOnlyList<PackPublishFile> files, long sequence, string archivePath, string publicRoot) {
        ValidateDetails(details);
        var snapshot = ReadBaseline(details.BaselineId);
        if (snapshot["files"] is not JArray baseline) throw new IOException("请先保存并选择整合包基线。");
        var manifest = new JObject {
            ["schema"] = PackProtocol.ArchiveRelease,
            ["pack"] = new JObject { ["id"] = details.PackId, ["name"] = details.Name },
            ["release"] = new JObject { ["id"] = details.ReleaseId, ["sequence"] = sequence, ["channel"] = details.Channel, ["notes"] = details.Notes },
            ["runtime"] = new JObject {
                ["minecraft"] = details.Minecraft, ["loader"] = new JObject { ["type"] = "neoforge", ["version"] = details.NeoForge },
                ["minecraft_change"] = "new-instance", ["allow_loader_downgrade"] = details.AllowDowngrade
            },
            ["components"] = new JArray(), ["files"] = new JArray(), ["actions"] = new JArray(),
            ["baseline"] = baseline.DeepClone(), ["cleanup"] = "previously-managed-unmodified"
        };
        if ((string?)snapshot["instance"] is string originalInstance) manifest["baseline_instance"] = originalInstance;
        var latest = (Latest["files"] as JArray ?? new JArray()).Cast<JObject>();
        var known = (details.BaselineId == null ? baseline.Cast<JObject>().Concat(latest) : latest.Concat(baseline.Cast<JObject>())).ToList();
        var online = new JArray();
        var offsets = new Dictionary<string, (long HeaderOffset, JObject Target)>();
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (var output = File.Create(archivePath))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8)) {
            foreach (var file in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)) {
                PackProtocol.ValidateRelative(file.Path, false);
                if (file.Operation == Exclude) continue;
                if (file.Operation == Delete) {
                    var old = known.LastOrDefault(f => string.Equals((string?)f["path"], file.Path, StringComparison.OrdinalIgnoreCase));
                    var action = new JObject { ["path"] = file.Path, ["action"] = "delete", ["expected_sha256"] = (string?)old?["sha256"] ?? file.Sha256,
                        ["reason"] = "服主标记的本次发布操作", ["force"] = true };
                    if (file.ModId is string id) action[file.FabricMod ? "fabric_id" : "id"] = id;
                    else if (old?["fabric_id"] is JToken fabricId) action["fabric_id"] = fabricId.DeepClone();
                    else if (old != null && PackProtocol.PrimaryForgeId(old) is string oldId) action["id"] = oldId;
                    ((JArray)manifest["actions"]!).Add(action);
                    continue;
                }
                if (file.LocalPath.Length == 0) {
                    if (file.Operation != Automatic) throw new IOException("需要实例中的源文件：" + file.Path);
                    continue;
                }
                if (file.Operation == Disable && !PackProtocol.ManagedMod(file.Path))
                    throw new InvalidDataException("只能禁用 Mod：" + file.Path);
                string target = file.Operation == Disable ? PackProtocol.EnabledModPath(file.Path) + ".disabled" : file.Path;
                bool embed = file.Category != "mod" || file.EmbedMod;
                if (!embed && file.Urls.Count == 0) throw new IOException("此 Mod 无平台下载源。请确认分发许可并勾选 ZIP 备用，或排除此文件：" + file.Path);
                var record = new JObject { ["path"] = target, ["size"] = file.Size, ["sha256"] = file.Sha256,
                    ["category"] = file.Category, ["urls"] = new JArray(file.Urls.Distinct()), ["write_policy"] = file.Operation == Preserve ? "preserve" : "unmodified" };
                if (file.ModId != null) record[file.FabricMod ? "fabric_id" : "id"] = file.ModId;
                if (file.Operation == Force) record["force"] = true;
                if (file.Operation == Disable) record["force_disabled"] = true;
                if (file.Optional) record["optional"] = true;
                if (file.Sources.Count > 0) record["sources"] = file.Sources.DeepClone();
                if (file.Operation == Patch) {
                    var keys = file.JsonKeys.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
                    if (keys.Length == 0) throw new InvalidDataException("请填写要修改的 JSON 配置项：" + file.Path);
                    PackJsonPatch.Verify(file.LocalPath, keys);
                    record["json_patch"] = new JObject { ["keys"] = new JArray(keys) };
                }
                ((JArray)manifest["files"]!).Add(record);
                using var input = File.OpenRead(file.LocalPath);
                if (input.Length != file.Size || PackProtocol.HashStream(input) != file.Sha256)
                    throw new IOException("预览后文件发生变化，请重新扫描：" + file.Path);
                input.Position = 0;
                if (embed) {
                    offsets.Add("overrides/" + target, (output.Position, record));
                    using var entry = CreateEntry(zip, "overrides/" + target).Open();
                    PackArchive.CopyBounded(input, entry, file.Size);
                } else {
                    var hashes = PackProtocol.PlatformHashes(input);
                    online.Add(new JObject { ["path"] = target, ["downloads"] = new JArray(file.Urls.Distinct()),
                        ["hashes"] = hashes, ["fileSize"] = file.Size,
                        ["env"] = new JObject { ["client"] = file.Optional ? "optional" : "required", ["server"] = "unsupported" } });
                }
            }
            using var writer = new StreamWriter(CreateEntry(zip, "modrinth.index.json").Open(), new UTF8Encoding(false));
            writer.Write(new JObject { ["game"] = "minecraft", ["formatVersion"] = 1, ["versionId"] = details.ReleaseId,
                ["name"] = details.Name, ["summary"] = details.Notes, ["files"] = online,
                ["dependencies"] = new JObject { ["minecraft"] = details.Minecraft, ["neoforge"] = details.NeoForge } }.ToString(Formatting.None));
        }
        using (var input = File.OpenRead(archivePath))
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true))
        using (var header = new BinaryReader(input, Encoding.UTF8, true)) {
            foreach (var entry in zip.Entries) {
                if (!offsets.TryGetValue(entry.FullName, out var match)) continue;
                input.Position = match.HeaderOffset;
                if (header.ReadUInt32() != 0x04034b50) throw new InvalidDataException("ZIP 本地条目偏移错误：" + entry.FullName);
                input.Position += 4;
                ushort method = header.ReadUInt16();
                input.Position += 16;
                int nameLength = header.ReadUInt16(), extraLength = header.ReadUInt16();
                match.Target["entry"] = new JObject { ["offset"] = input.Position + nameLength + extraLength, ["length"] = entry.CompressedLength,
                    ["method"] = method == 0 ? "store" : method == 8 ? "deflate" : throw new InvalidDataException("ZIP 压缩方式无法分段解码。") };
            }
        }
        string hash = PackProtocol.HashFile(archivePath);
        manifest["archive"] = new JObject { ["download_url"] = PublicUrl(publicRoot, ArchiveKey(hash)), ["size"] = new FileInfo(archivePath).Length, ["sha256"] = hash };
        PackProtocol.Validate(manifest, "release");
        return manifest;
    }

    private static ZipArchiveEntry CreateEntry(ZipArchive zip, string name) {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = ZipDate;
        return entry;
    }
    internal static string ArchiveKey(string hash) => "pcl/archives/" + hash + ".zip";
    internal static string ManifestKey(string pack, string channel, string release) => $"pcl/{pack}/{channel}/releases/{release}.json";
    internal static string PublicUrl(string root, string key) => root.TrimEnd('/') + "/" + key;
    internal void Remember(JObject manifest) => PackUpdater.Atomic(ProjectFile("latest.json"), manifest);
    public void Export(PackPublishDetails details, IReadOnlyList<PackPublishFile> files, string path) {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Build(details, files, 1, temporary, "https://localhost");
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void ValidateDetails(PackPublishDetails details) {
        foreach (string value in new[] { details.PackId, details.Channel, details.ReleaseId })
            ValidateIdentifier(value);
        if (string.IsNullOrWhiteSpace(details.Name)) throw new InvalidDataException("请填写整合包名称。");
    }
    internal static void ValidateIdentifier(string value) {
        if (!Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9._-]{0,127}\\z") || value.EndsWith(".", StringComparison.Ordinal))
            throw new InvalidDataException("请填写有效的整合包、频道或发布 ID，可使用英文小写、数字、点、连字符和下划线。");
    }
    private static bool SkipScannedName(string name) =>
        name.StartsWith(".", StringComparison.Ordinal) || name.Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private static void Walk(string directory, string relative, Action<string, string> add) {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("发布目录包含链接：" + directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
            var info = File.GetAttributes(path);
            string name = Path.GetFileName(path);
            if (SkipScannedName(name)) continue;
            if ((info & FileAttributes.ReparsePoint) != 0) throw new IOException("发布目录包含链接：" + path);
            string entry = relative.Length == 0 ? name : relative + "/" + name;
            if ((info & FileAttributes.Directory) != 0) Walk(path, entry, add);
            else add(entry, path);
        }
    }
    internal static (string Id, bool Fabric)? ReadPrimaryMod(string path) {
        using var input = File.OpenRead(path);
        return ReadPrimaryMod(input);
    }

    private static (string Id, bool Fabric)? ReadPrimaryMod(Stream input) {
        try {
            using var zip = new ZipArchive(input);
            var mods = ReadForgeModSections(zip);
            if (mods != null) return mods.Count != 0 && ReadForgeModId(mods[0]) is string id ? (id, false) : null;
            return ReadJsonModId(zip, "fabric.mod.json", "id") is string fabricId ? (fabricId, true) : null;
        } catch (InvalidDataException) { return null; }
    }

    public static string[] ReadDeclaredModIds(string path) {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try {
            using var input = File.OpenRead(path);
            using var zip = new ZipArchive(input);
            if (ReadForgeModSections(zip) is { } mods)
                foreach (Match mod in mods)
                    if (ReadForgeModId(mod) is string id) ids.Add(id);
            if (ReadJsonModId(zip, "fabric.mod.json", "id") is string fabricId) ids.Add(fabricId);
            if (ReadJsonModId(zip, "quilt.mod.json", "quilt_loader.id") is string quiltId) ids.Add(quiltId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return ids.ToArray();
    }

    private static string? ReadJsonModId(ZipArchive zip, string name, string key) {
        if (ReadModMetadata(zip.GetEntry(name)) is not string metadata) return null;
        try {
            var token = JObject.Parse(metadata).SelectToken(key);
            return token?.Type == JTokenType.String && token.Value<string>() is string id &&
                Regex.IsMatch(id, @"\A[a-z][a-z0-9_-]{1,63}\z") ? id : null;
        } catch (Newtonsoft.Json.JsonException) { return null; }
    }

    private static string? ReadModMetadata(ZipArchiveEntry? entry) {
        if (entry == null || entry.Length > 256 * 1024) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static MatchCollection? ReadForgeModSections(ZipArchive zip) {
        string? metadata = ReadModMetadata(zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml"));
        return metadata == null ? null : Regex.Matches(metadata, @"(?ms)^\s*\[\[mods\]\]\s*(?<section>.*?)(?=^\s*\[|\z)");
    }

    private static string? ReadForgeModId(Match mod) {
        var id = Regex.Match(mod.Groups["section"].Value, """(?m)^\s*modId\s*=\s*['"]([a-z][a-z0-9_]{1,63})['"]""");
        return id.Success ? id.Groups[1].Value : null;
    }
}
