using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Modpack;

/// <summary>Strict v1 protocol reader. No manifest-supplied code is executed.</summary>
public static class PackProtocol {
    public static JObject Parse(byte[] bytes) {
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))) { DateParseHandling = DateParseHandling.None, MaxDepth = 48 };
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("JSON 清单后存在多余内容。");
        return result;
    }
    public static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    public static string HashFile(string path) { using var sha = SHA256.Create(); using var input = File.OpenRead(path); return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
    public static void Https(string address) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("更新地址必须是 HTTPS 地址，且不能包含用户名、密码或片段。");
    }
    public static byte[] Fetch(string address, long max = 8 * 1024 * 1024) {
        using var output = new MemoryStream(); Download(address, output, max, null); return output.ToArray();
    }
    public static void Download(string address, Stream output, long max, Action<long>? progress) {
        // Validate every redirect: an HTTPS URL may not redirect to a local file or HTTP.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Modpack/1.0");
        for (int redirect = 0; redirect < 6; redirect++) {
            Https(address);
            using var response = client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) {
                address = new Uri(new Uri(address), response.Headers.Location ?? throw new IOException("更新服务器返回了无效跳转")).AbsoluteUri;
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            byte[] buffer = new byte[128 * 1024]; long total = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                total += read; if (total > max) throw new InvalidDataException("下载内容超过清单声明的大小。");
                output.Write(buffer, 0, read); progress?.Invoke(total);
            }
            return;
        }
        throw new IOException("更新服务器跳转次数过多。");
    }
    public static void Validate(JObject value, string kind) {
        using var stream = typeof(PackProtocol).Assembly.GetManifestResourceStream("Modpack." + kind + ".schema.json") ?? throw new InvalidOperationException("缺少更新协议资源");
        using var reader = new StreamReader(stream);
        Schema(value, JObject.Parse(reader.ReadToEnd()), "$", true);
        foreach (var prop in value.Descendants().OfType<JProperty>()) {
            if (prop.Name.EndsWith("_url")) Https((string)prop.Value);
            if (prop.Name == "urls") foreach (var url in (JArray)prop.Value) Https((string)url);
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
        Unique(paths, "文件路径");
        foreach (var path in paths) {
            ValidateRelative(path, false);
            if (paths.Any(other => path.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("文件与目录路径冲突。");
        }
        foreach (var item in records) if (item["component"] != null && !components.Any(c => (string?)c["id"] == (string?)item["component"])) throw new InvalidDataException("清单引用了未知组件。");
        foreach (var item in value["files"]!) if (string.Equals((string?)item["path"], "options.txt", StringComparison.OrdinalIgnoreCase) && (string?)item["write_policy"] != "preserve") throw new InvalidDataException("玩家设置必须采用保留策略。");
    }
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
                foreach (string key in schema["required"]?.Values<string>() ?? Enumerable.Empty<string>()) Require(obj[key] != null);
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
                if (schema["minLength"] != null) Require(((string)value).Length >= (int)schema["minLength"]!);
                if (schema["pattern"] != null) Require(Regex.IsMatch((string)value, (string)schema["pattern"]!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
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
