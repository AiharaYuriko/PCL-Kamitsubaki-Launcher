using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Modpack;

/// <summary>Strict v1 protocol reader. No manifest-supplied code is executed.</summary>
public static class PackProtocol {
    public const string ArchiveRelease = "pcl.modpack.release.v2";
    public static JObject Parse(byte[] bytes) {
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))) { DateParseHandling = DateParseHandling.None, MaxDepth = 48 };
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("JSON 清单后存在多余内容。");
        return result;
    }
    public static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    public static string HashFile(string path) { using var input = File.OpenRead(path); return HashStream(input); }
    internal static string HashStream(Stream input) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(input)); }
    internal static JObject PlatformHashes(Stream input) {
        using var sha1 = SHA1.Create();
        using var sha512 = SHA512.Create();
        string first = Hex(sha1.ComputeHash(input));
        input.Position = 0;
        return new JObject { ["sha1"] = first, ["sha512"] = Hex(sha512.ComputeHash(input)) };
    }
    internal static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    public static void Https(string address) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("更新地址必须是 HTTPS 地址，且不能包含用户名、密码或片段。");
    }
    public static byte[] Fetch(string address, long max = 8 * 1024 * 1024) {
        using var output = new MemoryStream(); Download(address, output, max, null); return output.ToArray();
    }
    public static void Download(string address, Stream output, long max, Action<long>? progress) =>
        Transfer(address, output, max, progress, null, 0);
    internal static void DownloadRange(string address, Stream output, long offset, long length, long total) =>
        Transfer(address, output, length, null, offset, total);
    internal static void CheckPublicSize(string address, long size) {
        Https(address);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(new HttpMethod("HEAD"), address);
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength != size) throw new IOException("公开 ZIP 的大小与上传内容不一致。");
    }
    private static void Transfer(string address, Stream output, long max, Action<long>? progress, long? offset, long objectSize) {
        // Validate every redirect: an HTTPS URL may not redirect to a local file or HTTP.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Modpack/1.0");
        for (int redirect = 0; redirect < 6; redirect++) {
            Https(address);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (offset.HasValue) {
                request.Headers.Range = new RangeHeaderValue(offset.Value, checked(offset.Value + max - 1));
                request.Headers.AcceptEncoding.ParseAdd("identity");
            }
            using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null) {
                address = new Uri(new Uri(address), response.Headers.Location ?? throw new IOException("更新服务器返回了无效跳转")).AbsoluteUri;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (offset.HasValue) {
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                    range.From != offset.Value || range.To != offset.Value + max - 1 || range.Length != objectSize ||
                    response.Content.Headers.ContentEncoding.Any())
                    throw new IOException("下载域名未返回有效的 ZIP 分段。");
            }
            using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            byte[] buffer = new byte[128 * 1024]; long total = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                total += read; if (total > max) throw new InvalidDataException("下载内容超过清单声明的大小。");
                output.Write(buffer, 0, read); progress?.Invoke(total);
            }
            if (offset.HasValue && total != max) throw new EndOfStreamException("ZIP 分段下载不完整。");
            return;
        }
        throw new IOException("更新服务器跳转次数过多。");
    }
    public static void Validate(JObject value, string kind) {
        using var stream = typeof(PackProtocol).Assembly.GetManifestResourceStream("Modpack." + kind + ".schema.json") ?? throw new InvalidOperationException("缺少更新协议资源");
        using var reader = new StreamReader(stream);
        var schema = JObject.Parse(reader.ReadToEnd());
        bool archived = kind == "release" && (string?)value["schema"] == ArchiveRelease;
        if (archived) ExtendReleaseSchema(schema);
        Schema(value, schema, "$", true);
        foreach (var prop in value.Descendants().OfType<JProperty>()) {
            if (prop.Name.EndsWith("_url", StringComparison.Ordinal)) Https(prop.Value.Value<string>()!);
            if (prop.Name == "urls") foreach (var url in (JArray)prop.Value) Https(url.Value<string>()!);
        }
        if (kind == "index") {
            Unique(value["packs"]!.Select(p => (string)p["id"]!), "整合包 ID");
            foreach (var pack in value["packs"]!) Unique(pack["channels"]!.Select(c => (string)c["id"]!), "频道 ID");
            return;
        }
        var components = value["components"]!.ToList();
        Unique(components.Select(c => (string)c["id"]!), "组件 ID");
        if (components.Any(c => (bool)c["required"]! && !(bool)c["default_selected"]!)) throw new InvalidDataException("必要组件必须默认选中。");
        var records = value["files"]!.Concat(value["actions"]!).ToList();
        var paths = records.Select(i => (string)i["path"]!).ToList();
        paths.AddRange(value["actions"]!.Where(a => (string?)a["action"] == "disable").Select(a => (string)a["path"]! + ".disabled"));
        var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count != paths.Count) throw new InvalidDataException("文件路径重复。");
        foreach (var path in paths) {
            ValidateRelative(path, false);
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (pathSet.Contains(path.Substring(0, slash))) throw new InvalidDataException("文件与目录路径冲突。");
        }
        foreach (var item in records) if (item["component"] != null && !components.Any(c => (string?)c["id"] == (string?)item["component"])) throw new InvalidDataException("清单引用了未知组件。");
        foreach (var item in value["files"]!) if (string.Equals((string?)item["path"], "options.txt", StringComparison.OrdinalIgnoreCase) && (string?)item["write_policy"] != "preserve") throw new InvalidDataException("玩家设置必须采用保留策略。");
        if (archived) ValidateArchiveRelease(value);
    }
    private static void ExtendReleaseSchema(JObject schema) {
        const string extensions = """
          {
            "archive": {
              "type":"object", "additionalProperties":false, "required":["download_url","size","sha256"],
              "properties":{"download_url":{"type":"string"},"size":{"type":"integer","minimum":22},"sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"}}
            },
            "baseline": {
              "type":"array", "items":{"type":"object","additionalProperties":false,"required":["path","sha256"],"properties":{
                "path":{"type":"string"}, "sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"}, "id":{"type":"string","pattern":"^[a-z][a-z0-9_]{1,63}$"}
              }}
            },
            "baseline_instance":{"type":"string","minLength":1},
            "file": {
              "entry":{"type":"object","additionalProperties":false,"required":["offset","length","method"],"properties":{
                "offset":{"type":"integer","minimum":30},"length":{"type":"integer","minimum":0},"method":{"enum":["store","deflate"]}
              }},
              "force":{"type":"boolean"},
              "force_disabled":{"type":"boolean"},
              "optional":{"type":"boolean"},
              "id":{"type":"string","pattern":"^[a-z][a-z0-9_]{1,63}$"},
              "sources":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["platform","project","version"],"properties":{
                "platform":{"enum":["curseforge","modrinth"]}, "project":{"type":"string","pattern":"^[A-Za-z0-9]+$"}, "version":{"type":"string","pattern":"^[A-Za-z0-9]+$"}
              }}},
              "json_patch":{"type":"object","additionalProperties":false,"required":["keys"],"properties":{
                "keys":{"type":"array","minItems":1,"uniqueItems":true,"items":{"type":"string","minLength":1}}
              }}
            }
          }
          """;
        var ext = JObject.Parse(extensions);
        var props = (JObject)schema["properties"]!;
        props["schema"]!["const"] = ArchiveRelease;
        foreach (string name in new[] { "archive", "baseline" }) {
            props[name] = ext[name]!.DeepClone();
            ((JArray)schema["required"]!).Add(name);
        }
        props["baseline_instance"] = ext["baseline_instance"]!.DeepClone();
        var fileProps = (JObject)props["files"]!["items"]!["properties"]!;
        foreach (var prop in ((JObject)ext["file"]!).Properties()) fileProps[prop.Name] = prop.Value.DeepClone();
        fileProps["urls"]!["minItems"] = 0;
        ((JArray)fileProps["category"]!["enum"]!).Add("map");
        fileProps["write_policy"]!["enum"] = new JArray("unmodified", "preserve");
        var actionItem = props["actions"]!["items"]!;
        actionItem["properties"]!["force"] = new JObject { ["type"] = "boolean" };
        actionItem["properties"]!["id"] = fileProps["id"]!.DeepClone();
    }
    private static void ValidateArchiveRelease(JObject value) {
        long archiveSize = (long)value["archive"]!["size"]!;
        var baseline = value["baseline"]!.Select(f => (string)f["path"]!).ToArray();
        Unique(baseline, "旧整合包文件路径");
        foreach (var path in baseline) ValidateRelative(path, false);
        Unique(baseline.Where(ManagedMod).Select(EnabledModPath), "基线 Mod 路径");
        var ranges = new List<(long Offset, long Length)>();
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in value["files"]!) {
            string path = (string)file["path"]!;
            if (ManagedMod(path) && !mods.Add(EnabledModPath(path))) throw new InvalidDataException("同一 Mod 有两个启用状态：" + path);
            if (file["id"] != null && !ManagedMod(path)) throw new InvalidDataException("只有 Mod 可以声明 Mod ID。");
            if ((bool?)file["force_disabled"] == true && (!ManagedMod(path) || !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("强制禁用的目标必须是禁用状态的 Mod。");
            if (file["json_patch"] != null && (!path.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || file["json_patch"]!["keys"]!.Values<string>().Any(k => k?.StartsWith("/", StringComparison.Ordinal) != true)))
                throw new InvalidDataException("配置项操作需要 config 下的 JSON 文件和 JSON Pointer。");
            if (file["entry"] is not JObject entry) {
                if (!file["urls"]!.Any()) throw new InvalidDataException("文件没有平台地址或 ZIP 条目：" + path);
                continue;
            }
            long offset = (long)entry["offset"]!, length = (long)entry["length"]!;
            if (offset > archiveSize || length > archiveSize - offset || length == 0 && (long)file["size"]! != 0 ||
                (string?)entry["method"] == "store" && length != (long)file["size"]!)
                throw new InvalidDataException("ZIP 条目超出边界：" + path);
            ranges.Add((offset, length));
        }
        foreach (var action in value["actions"]!) {
            string path = (string)action["path"]!;
            if (action["id"] != null && !ManagedMod(path)) throw new InvalidDataException("只有 Mod 操作能声明 Mod ID。");
            if (ManagedMod(path) && !mods.Add(EnabledModPath(path))) throw new InvalidDataException("同一 Mod 文件存在多个更新操作：" + path);
        }
        long previousEnd = 0;
        foreach (var range in ranges.OrderBy(r => r.Offset)) {
            if (range.Offset < previousEnd) throw new InvalidDataException("ZIP 条目的字节范围重叠。");
            previousEnd = range.Offset + range.Length;
        }
    }
    internal static bool ManagedMod(string path) => path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
    internal static string EnabledModPath(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 9) : path;
    private static void Unique(IEnumerable<string> values, string name) { var list = values.ToList(); if (list.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count) throw new InvalidDataException(name + "重复。"); }
    // This evaluator intentionally only supports the vocabulary used by the embedded schemas.
    private static bool Schema(JToken value, JObject schema, string at, bool fail) {
        try {
            void Require(bool condition) { if (!condition) throw new InvalidDataException("更新清单格式错误：" + at); }
            if (schema["const"] != null) Require(JToken.DeepEquals(value, schema["const"]));
            if (schema["enum"] is JArray choices) Require(choices.Any(c => JToken.DeepEquals(c, value)));
            string? type = (string?)schema["type"];
            if (type != null) Require(type switch { "object" => value.Type == JTokenType.Object, "array" => value.Type == JTokenType.Array, "string" => value.Type == JTokenType.String, "integer" => value.Type == JTokenType.Integer, "boolean" => value.Type == JTokenType.Boolean, _ => false });
            if (value is JObject obj) {
                if (schema["required"] is JArray required)
                    foreach (var key in required) Require(obj[key.Value<string>()!] != null);
                var props = schema["properties"] as JObject;
                foreach (var prop in obj.Properties()) {
                    if (props?[prop.Name] is JObject sub) Schema(prop.Value, sub, at + "." + prop.Name, true);
                    else if ((bool?)schema["additionalProperties"] == false) Require(false);
                }
            }
            if (value is JArray array) {
                if (schema["minItems"] != null) Require(array.Count >= (int)schema["minItems"]!);
                if ((bool?)schema["uniqueItems"] == true) Require(array.Select(x => x.ToString(Formatting.None)).Distinct().Count() == array.Count);
                if (schema["items"] is JObject sub) foreach (var item in array) Schema(item, sub, at + "[]", true);
            }
            if (value.Type == JTokenType.String) {
                string item = value.Value<string>()!;
                if (schema["minLength"] != null) Require(item.Length >= (int)schema["minLength"]!);
                if (schema["pattern"] != null) Require(Regex.IsMatch(item, (string)schema["pattern"]!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            }
            if (schema["minimum"] != null) Require((long)value >= (long)schema["minimum"]!);
            if (schema["allOf"] is JArray rules) foreach (JObject rule in rules) {
                if (rule["if"] is JObject when && Schema(value, when, at, false)) Schema(value, (JObject)rule["then"]!, at, true);
            }
            return true;
        } catch (InvalidDataException) { if (fail) throw; return false; }
    }
    public static void ValidateRelative(string path, bool internalFile) {
        var parts = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || Regex.IsMatch(p, @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$", RegexOptions.IgnoreCase)) || path.Any(c => c < 32 || ":*?\"<>|".Contains(c))) throw new InvalidDataException("无效的文件路径：" + path);
        var reserved = new[] { "pcl", ".pcl-update", ".mcpack-client", "libraries", "assets", "natives", "saves", "logs", "crash-reports", "versions" };
        if (!internalFile && (reserved.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || (parts.Length == 1 && (Regex.IsMatch(path, @"\.(json|jar|exe|dll)$", RegexOptions.IgnoreCase) || path.Equals("pcl.ini", StringComparison.OrdinalIgnoreCase))))) throw new InvalidDataException("更新不能覆盖受保护的文件：" + path);
    }
    public static string Safe(string root, string relative, bool internalFile = false) {
        ValidateRelative(relative, internalFile);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出实例目录。");
        for (string? p = target; p != null; p = Path.GetDirectoryName(p)) {
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新路径包含符号链接或目录联接：" + p);
        }
        return target;
    }
}
