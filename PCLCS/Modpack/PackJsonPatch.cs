using System.IO;

namespace PCL.Modpack;

internal static class PackJsonPatch {
    internal static void Apply(string local, string source, string output, IEnumerable<string> keys) {
        var original = Read(local);
        var published = Read(source);
        foreach (string key in keys) {
            var segments = Segments(key);
            JToken? value = published;
            foreach (string segment in segments) value = (value as JObject)?[segment];
            if (value == null) throw new InvalidDataException("发布配置中缺少指定项：" + key);
            JObject destination = original;
            foreach (string segment in segments.Take(segments.Length - 1)) {
                if (destination[segment] == null) destination[segment] = new JObject();
                destination = destination[segment] as JObject ?? throw new InvalidDataException("本地 JSON 配置项不是对象：" + key);
            }
            destination[segments.Last()] = value.DeepClone();
        }
        File.WriteAllText(output, original.ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static void Verify(string source, IEnumerable<string> keys) {
        var published = Read(source);
        foreach (string key in keys) {
            JToken? value = published;
            foreach (string segment in Segments(key)) value = (value as JObject)?[segment];
            if (value == null) throw new InvalidDataException("发布配置中缺少指定项：" + key);
        }
    }

    private static JObject Read(string path) =>
        new FileInfo(path).Length <= 8 * 1024 * 1024 ? PackProtocol.Parse(File.ReadAllBytes(path)) : throw new InvalidDataException("JSON 配置大于 8 MB。");

    private static string[] Segments(string key) {
        if (!key.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(key, "~(?![01])"))
            throw new InvalidDataException("无效的 JSON Pointer：" + key);
        return key.Substring(1).Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();
    }
}
