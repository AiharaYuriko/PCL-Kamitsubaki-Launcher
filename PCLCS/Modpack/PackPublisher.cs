using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PCL.Modpack;

public sealed class PackPublishFile {
    public string Path { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? ModId { get; set; }
    public long Size { get; set; }
    public string Category { get; set; } = "file";
    public string Change { get; set; } = "";
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
}

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

    internal string ProjectFile(string name) => PackProtocol.Safe(root, ".pcl-update/publisher/" + name, true);
    public JObject Settings => Read("settings.json");
    public void SaveSettings(JObject settings) => PackUpdater.Atomic(ProjectFile("settings.json"), settings);
    public bool HasBaseline => File.Exists(ProjectFile("baseline.json"));
    public JObject Latest => Read("latest.json");

    public static string? PublishedSource(JObject manifest) {
        string? archive = (string?)manifest["archive"]?["download_url"];
        string? hash = (string?)manifest["archive"]?["sha256"];
        if (archive == null || hash == null) return null;
        string suffix = "/" + ArchiveKey(hash);
        return archive.EndsWith(suffix, StringComparison.Ordinal) ? archive.Substring(0, archive.Length - suffix.Length) : null;
    }
    public JObject ReadCredentials(bool mirror = false) {
        var saved = (string?)Read(mirror ? "mirror-credentials.json" : "credentials.json")["encrypted"];
        return saved == null ? new JObject() : PackProtocol.Parse(ProtectedData.Unprotect(Convert.FromBase64String(saved), null, DataProtectionScope.CurrentUser));
    }
    public void SaveCredentials(string access, string secret, bool mirror = false) {
        string path = ProjectFile(mirror ? "mirror-credentials.json" : "credentials.json");
        if (access.Length == 0 || secret.Length == 0) { File.Delete(path); return; }
        var bytes = PackR2Publisher.JsonBytes(new JObject { ["access"] = access, ["secret"] = secret });
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        PackUpdater.Atomic(path, new JObject { ["encrypted"] = Convert.ToBase64String(encrypted) });
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

    public List<PackPublishFile> Scan(IEnumerable<string> folders, bool compare = true, IEnumerable<string>? files = null) {
        var result = new Dictionary<string, PackPublishFile>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files ?? Enumerable.Empty<string>()) {
            string path = file.Replace('\\', '/');
            Add(path, ResolveImportFile(path));
        }
        foreach (string folder in folders.Select(f => f.Trim().Replace('\\', '/').TrimEnd('/')).Where(f => f.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)) {
            string full = ResolveScanPath(folder);
            if (File.Exists(full)) Add(folder, full);
            else if (Directory.Exists(full)) Walk(full, folder, Add);
        }
        foreach (JObject map in ReadMaps()) {
            string published = (string)map["path"]!;
            string source = ProjectFile("maps/" + (string)map["snapshot"]!);
            if ((bool?)map["file"] == true) Add(published, source);
            else Walk(source, published, Add);
        }
        if (compare) AddChanges(result);
        return result.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string path, string full) {
            PackProtocol.Safe(root, path);
            if (result.ContainsKey(path)) return;
            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && !PackProtocol.ManagedMod(path)) return;
            bool map = IsMapPath(path);
            var info = new FileInfo(full);
            result.Add(path, new PackPublishFile {
                Path = path, LocalPath = full, Sha256 = PackProtocol.HashFile(full), Size = info.Length,
                ModId = PackProtocol.ManagedMod(path) ? ReadModId(full) : null,
                Category = map ? "map" : path.Split('/')[0].ToLowerInvariant() switch {
                    "mods" => "mod", "config" or "defaultconfigs" => "config", "kubejs" or "scripts" => "script",
                    "resourcepacks" => "resourcepack", "shaderpacks" => "shaderpack", _ => "file"
                },
                Operation = map ? Preserve : Automatic
            });
        }
    }

    public void SaveBaseline(IEnumerable<PackPublishFile> files) {
        if (HasBaseline) throw new IOException("此实例已建立基线。");
        var included = files.Where(f => f.LocalPath.Length > 0 && f.Category != "map" && f.Operation != Exclude).ToList();
        foreach (var file in included)
            if (!PackArchive.Matches(PackProtocol.Safe(root, file.Path), file.Size, file.Sha256))
                throw new IOException("扫描后文件发生变化，请重新扫描：" + file.Path);
        var snapshot = included.Select(Snapshot).ToList();
        if (snapshot.Count == 0) throw new IOException("请选择至少一个旧整合包文件。");
        PackUpdater.Atomic(ProjectFile("baseline.json"), new JObject {
            ["instance"] = Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)), ["files"] = new JArray(snapshot)
        });
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
    private JArray Baseline => Read("baseline.json")["files"] as JArray ?? throw new IOException("请先建立旧整合包基线。");
    private List<JObject> KnownFiles() => Baseline.Cast<JObject>().Concat((Read("latest.json")["files"] as JArray ?? new JArray()).Cast<JObject>()).ToList();

    private void AddChanges(Dictionary<string, PackPublishFile> result) {
        if (!HasBaseline) {
            foreach (var file in result.Values) file.Change = "基线候选";
            return;
        }
        var previous = (Read("latest.json")["files"] as JArray ?? Baseline).Cast<JObject>()
            .ToDictionary(f => (string)f["path"]!, StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Values) file.Change = !previous.TryGetValue(file.Path, out var old) ? "新增" :
            file.Sha256 == (string?)old["sha256"] ? "相同" : "更新";
        foreach (var pair in previous) {
            if (result.ContainsKey(pair.Key) || IsMapPath(pair.Key)) continue;
            result.Add(pair.Key, new PackPublishFile { Path = pair.Key, Sha256 = (string)pair.Value["sha256"]!, Change = "已移出" });
        }
    }

    private static JObject Snapshot(PackPublishFile file) {
        var entry = new JObject { ["path"] = file.Path, ["sha256"] = file.Sha256 };
        if (file.ModId != null) entry["id"] = file.ModId;
        return entry;
    }

    internal JObject Build(PackPublishDetails details, IReadOnlyList<PackPublishFile> files, long sequence, string archivePath, string publicRoot) {
        ValidateDetails(details);
        var manifest = new JObject {
            ["schema"] = PackProtocol.ArchiveRelease,
            ["pack"] = new JObject { ["id"] = details.PackId, ["name"] = details.Name },
            ["release"] = new JObject { ["id"] = details.ReleaseId, ["sequence"] = sequence, ["channel"] = details.Channel, ["notes"] = details.Notes },
            ["runtime"] = new JObject {
                ["minecraft"] = details.Minecraft, ["loader"] = new JObject { ["type"] = "neoforge", ["version"] = details.NeoForge },
                ["minecraft_change"] = "new-instance", ["allow_loader_downgrade"] = details.AllowDowngrade
            },
            ["components"] = new JArray(), ["files"] = new JArray(), ["actions"] = new JArray(),
            ["baseline"] = Baseline.DeepClone(), ["cleanup"] = "previously-managed-unmodified"
        };
        if ((string?)Read("baseline.json")["instance"] is string originalInstance) manifest["baseline_instance"] = originalInstance;
        var known = KnownFiles();
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
                    if ((file.ModId ?? (string?)old?["id"]) is string id) action["id"] = id;
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
                if (file.ModId != null) record["id"] = file.ModId;
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
            if (!Regex.IsMatch(value, "^[a-z0-9][a-z0-9._-]{0,127}$") || value.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidDataException("整合包、频道和发布 ID 只能使用英文小写、数字、点、连字符和下划线。");
        if (string.IsNullOrWhiteSpace(details.Name)) throw new InvalidDataException("请填写整合包名称。");
    }
    private static void Walk(string directory, string relative, Action<string, string> add) {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("发布目录包含链接：" + directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
            var info = File.GetAttributes(path);
            string name = Path.GetFileName(path);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            if ((info & FileAttributes.ReparsePoint) != 0) throw new IOException("发布目录包含链接：" + path);
            string entry = relative.Length == 0 ? name : relative + "/" + name;
            if ((info & FileAttributes.Directory) != 0) Walk(path, entry, add);
            else add(entry, path);
        }
    }
    internal static string? ReadModId(string path) {
        try {
            using var input = File.OpenRead(path);
            using var zip = new ZipArchive(input);
            var entry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (entry == null || entry.Length > 256 * 1024) return null;
            using var reader = new StreamReader(entry.Open());
            var mods = Regex.Matches(reader.ReadToEnd(), @"(?ms)^\s*\[\[mods\]\]\s*(?<section>.*?)(?=^\s*\[|\z)");
            if (mods.Count != 1) return null;
            var id = Regex.Match(mods[0].Groups["section"].Value, """(?m)^\s*modId\s*=\s*['"]([a-z][a-z0-9_]{1,63})['"]""");
            return id.Success ? id.Groups[1].Value : null;
        } catch (InvalidDataException) { return null; }
    }
}
