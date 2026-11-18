using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace PCL.Modpack;

public sealed class PackR2Settings {
    public string Account { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string PublicRoot { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    internal string Host => Account + ".r2.cloudflarestorage.com";

    internal void Validate() {
        if (!Regex.IsMatch(Account, @"^[a-f0-9]{32}(?:\.(?:eu|us|fedramp))?$") ||
            !Regex.IsMatch(Bucket, @"^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$"))
            throw new InvalidDataException("请检查 R2 Account ID 和存储桶名称。");
        PackProtocol.Https(PublicRoot);
        var origin = new Uri(PublicRoot);
        if (origin.AbsolutePath != "/" || origin.Query.Length != 0) throw new InvalidDataException("公开地址应为 R2 自定义域名的根地址。");
        if (!Regex.IsMatch(AccessKey, "^[a-zA-Z0-9]+$") || string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidDataException("请填写 R2 S3 Access Key ID 和 Secret Access Key。");
    }
}

public sealed record PackPublishResult(string IndexAddress, bool SupportsRanges);

public sealed class PackR2Publisher(PackPublisher publisher) {
    private const string IndexKey = "pcl/index.json";

    public PackPublishResult Publish(PackPublishDetails details, PackR2Settings settings, IReadOnlyList<PackPublishFile> files, Action<string> progress) {
        settings.Validate();
        PackPublisher.ValidateDetails(details);
        using var r2 = new R2Objects(settings);
        var previous = r2.Read(IndexKey);
        var index = previous.Bytes != null ? PackProtocol.Parse(previous.Bytes) : new JObject {
            ["schema"] = "pcl.modpack.index.v1", ["name"] = details.Name, ["packs"] = new JArray()
        };
        PackProtocol.Validate(index, "index");
        var packs = (JArray)index["packs"]!;
        var pack = packs.OfType<JObject>().SingleOrDefault(p => (string?)p["id"] == details.PackId);
        if (pack == null) {
            pack = new JObject { ["id"] = details.PackId, ["name"] = details.Name, ["channels"] = new JArray() };
            packs.Add(pack);
        }
        var channels = (JArray)pack["channels"]!;
        var current = channels.OfType<JObject>().SingleOrDefault(c => (string?)c["id"] == details.Channel);
        bool retry = (string?)current?["release_id"] == details.ReleaseId;
        long sequence = (long?)current?["sequence"] ?? 0;
        if (!retry) sequence = checked(sequence + 1);
        var localLatest = publisher.Latest;
        if (current != null && !retry && (long?)localLatest["release"]?["sequence"] != (long?)current["sequence"])
            progress("检测到已有频道，使用服务器当前序号继续发布。");
        string local = publisher.ProjectFile("release.zip");
        try {
            progress("正在生成并校验完整 ZIP……");
            var manifest = publisher.Build(details, files, sequence, local, settings.PublicRoot);
            byte[] bytes = JsonBytes(manifest);
            if (bytes.Length > 8 * 1024 * 1024) throw new IOException("版本清单超过客户端的下载上限。");
            string manifestHash = PackProtocol.Hash(bytes);
            string manifestKey = $"pcl/{details.PackId}/{details.Channel}/releases/{details.ReleaseId}.json";
            string manifestUrl = PackPublisher.PublicUrl(settings.PublicRoot, manifestKey);
            if (retry && (string?)current?["manifest_sha256"] != manifestHash)
                throw new IOException("该发布 ID 已经上线。请为不同内容使用新的发布 ID。");
            string archiveHash = (string)manifest["archive"]!["sha256"]!;
            string archiveUrl = (string)manifest["archive"]!["download_url"]!;
            string archiveKey = PackPublisher.ArchiveKey(archiveHash);
            long size = (long)manifest["archive"]!["size"]!;
            r2.PutArchive(archiveKey, local, size, archiveHash, progress);
            progress("正在提交版本清单并验证公开下载……");
            r2.PutImmutable(manifestKey, bytes);
            if (PackProtocol.Hash(PackProtocol.Fetch(manifestUrl)) != manifestHash) throw new IOException("公开版本清单与上传内容不一致。");
            PackProtocol.CheckPublicSize(archiveUrl, size);
            bool supportsRanges;
            try {
                using var probe = new MemoryStream();
                PackProtocol.DownloadRange(archiveUrl, probe, 0, 2, size);
                if (!probe.ToArray().SequenceEqual(new byte[] { 0x50, 0x4b })) throw new IOException("公开域名的 ZIP 分段数据与上传文件不符。");
                supportsRanges = true;
            } catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) {
                supportsRanges = false;
                progress("当前域名的分段探测失败，玩家可使用完整 ZIP：" + ex.Message);
            }
            if (!retry) {
                pack["name"] = details.Name;
                var pointer = new JObject { ["id"] = details.Channel, ["release_id"] = details.ReleaseId, ["sequence"] = sequence,
                    ["manifest_url"] = manifestUrl, ["manifest_sha256"] = manifestHash };
                if (current == null) channels.Add(pointer);
                else current.Replace(pointer);
                PackProtocol.Validate(index, "index");
                progress("正在切换正式频道……");
                try {
                    r2.PutIndex(IndexKey, JsonBytes(index), previous.ETag);
                } catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) {
                    if (!r2.PointsTo(IndexKey, details, manifestHash)) throw;
                }
            }
            publisher.Remember(manifest);
            return new PackPublishResult(PackPublisher.PublicUrl(settings.PublicRoot, IndexKey), supportsRanges);
        } finally {
            if (File.Exists(local)) File.Delete(local);
        }
    }

    internal static byte[] JsonBytes(JObject value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));

    private sealed class R2Objects(PackR2Settings settings) : IDisposable {
        private const int PartSize = 32 * 1024 * 1024;
        private const string ImmutableCache = "public, max-age=31536000, immutable";
        private static readonly HttpMethod Head = new("HEAD");
        private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };

        internal (byte[]? Bytes, string? ETag) Read(string key) {
            using var response = Send(HttpMethod.Get, key);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
            Ensure(response, "读取清单");
            using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                if (output.Length + read > 8 * 1024 * 1024) throw new IOException("R2 清单超过下载限制。");
                output.Write(buffer, 0, read);
            }
            return (output.ToArray(), response.Headers.ETag?.ToString() ?? throw new IOException("R2 未返回清单 ETag。"));
        }
        internal void PutIndex(string key, byte[] bytes, string? etag) {
            using var response = Send(HttpMethod.Put, key, body: bytes, type: "application/json", cache: "no-store",
                condition: etag == null ? "if-none-match" : "if-match", conditionValue: etag ?? "*");
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new IOException("频道已被其他发布修改，请重新读取后重试。");
            Ensure(response, "切换频道");
        }
        internal bool PointsTo(string key, PackPublishDetails details, string hash) {
            try {
                byte[]? bytes = Read(key).Bytes;
                if (bytes == null) return false;
                var index = PackProtocol.Parse(bytes);
                PackProtocol.Validate(index, "index");
                var pack = index["packs"]!.SingleOrDefault(p => (string?)p["id"] == details.PackId);
                var channel = pack?["channels"]?.SingleOrDefault(c => (string?)c["id"] == details.Channel);
                return (string?)channel?["release_id"] == details.ReleaseId && (string?)channel?["manifest_sha256"] == hash;
            } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or HttpRequestException or OperationCanceledException) {
                return false;
            }
        }
        internal void PutImmutable(string key, byte[] bytes) {
            using var response = Send(HttpMethod.Put, key, body: bytes, type: "application/json", cache: ImmutableCache,
                condition: "if-none-match", conditionValue: "*");
            if (response.StatusCode == HttpStatusCode.PreconditionFailed) {
                if (Read(key).Bytes?.SequenceEqual(bytes) == true) return;
                throw new IOException("同一发布 ID 已有不同的清单，请使用新的发布 ID。");
            }
            Ensure(response, "上传版本清单");
        }

        internal void PutArchive(string key, string path, long size, string hash, Action<string> progress) {
            if (HasArchive(key, size, hash)) return;
            if (size <= PartSize) {
                using var put = Send(HttpMethod.Put, key, body: File.ReadAllBytes(path), type: "application/zip", cache: ImmutableCache,
                    condition: "if-none-match", conditionValue: "*", metadata: hash);
                if (put.StatusCode == HttpStatusCode.PreconditionFailed && HasArchive(key, size, hash)) return;
                Ensure(put, "上传 ZIP");
                return;
            }
            long partSize = Math.Max(PartSize, (size + 9999) / 10000);
            if (partSize > 512 * 1024 * 1024) throw new IOException("ZIP 超过当前上传器支持的大小。");
            using var create = Send(HttpMethod.Post, key, "uploads=", type: "application/zip", cache: ImmutableCache,
                metadata: hash, condition: "if-none-match", conditionValue: "*");
            if (create.StatusCode == HttpStatusCode.PreconditionFailed && HasArchive(key, size, hash)) return;
            Ensure(create, "初始化分片上传");
            string id = ReadXml(create).Elements().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value
                ?? throw new IOException("R2 未返回分片上传 ID。");
            string upload = "uploadId=" + Uri.EscapeDataString(id);
            bool completed = false;
            try {
                var parts = new XElement("CompleteMultipartUpload");
                using var input = File.OpenRead(path);
                for (int number = 1; input.Position < size; number++) {
                    var data = new byte[(int)Math.Min(partSize, size - input.Position)];
                    int offset = 0;
                    while (offset < data.Length) {
                        int count = input.Read(data, offset, data.Length - offset);
                        if (count == 0) throw new EndOfStreamException("ZIP 在分片上传期间发生变化。");
                        offset += count;
                    }
                    progress($"正在上传完整 ZIP：{input.Position / 1048576d:F0} / {size / 1048576d:F0} MB（第 {number} 片）");
                    string part = "partNumber=" + number.ToString(CultureInfo.InvariantCulture) + "&" + upload;
                    string etag = RetryPart(key, part, data);
                    parts.Add(new XElement("Part", new XElement("PartNumber", number), new XElement("ETag", etag)));
                }
                using var commit = Send(HttpMethod.Post, key, upload, Encoding.UTF8.GetBytes(parts.ToString(SaveOptions.DisableFormatting)),
                    "application/xml", condition: "if-none-match", conditionValue: "*");
                if (commit.StatusCode == HttpStatusCode.PreconditionFailed && HasArchive(key, size, hash)) return;
                Ensure(commit, "提交 ZIP 分片");
                if (ReadXml(commit).Name.LocalName != "CompleteMultipartUploadResult") throw new IOException("R2 未确认 ZIP 分片提交。");
                completed = true;
            } finally {
                if (!completed) {
                    try { using var abandon = Send(HttpMethod.Delete, key, upload); }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) { }
                }
            }
        }

        private string RetryPart(string key, string query, byte[] data) {
            for (int attempt = 0; ; attempt++) {
                try {
                    using var put = Send(HttpMethod.Put, key, query, data);
                    if (attempt < 2 && ((int)put.StatusCode >= 500 || (int)put.StatusCode == 429)) continue;
                    Ensure(put, "上传 ZIP 分片");
                    return put.Headers.ETag?.ToString() ?? throw new IOException("R2 未返回分片 ETag。");
                } catch (HttpRequestException) when (attempt < 2) { }
                catch (OperationCanceledException) when (attempt < 2) { }
            }
        }

        private bool HasArchive(string key, long size, string hash) {
            using var response = Send(Head, key);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            Ensure(response, "验证 ZIP 对象");
            if (response.Content.Headers.ContentLength != size || !response.Headers.TryGetValues("x-amz-meta-sha256", out var checksums) || !checksums.Contains(hash))
                throw new IOException("R2 已有同名但校验信息不同的 ZIP。");
            return true;
        }

        private HttpResponseMessage Send(HttpMethod method, string key, string query = "", byte[]? body = null, string? type = null,
            string? cache = null, string? condition = null, string? conditionValue = null, string? metadata = null) {
            DateTime time = DateTime.UtcNow;
            string day = time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string stamp = time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string payload = PackProtocol.Hash(body ?? Array.Empty<byte>());
            string path = "/" + settings.Bucket + "/" + string.Join("/", key.Split('/').Select(Uri.EscapeDataString));
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) {
                ["host"] = settings.Host, ["x-amz-content-sha256"] = payload, ["x-amz-date"] = stamp
            };
            if (condition != null) headers[condition] = conditionValue!;
            if (metadata != null) headers["x-amz-meta-sha256"] = metadata;
            string signed = string.Join(";", headers.Keys);
            string canonical = method.Method + "\n" + path + "\n" + query + "\n" +
                string.Concat(headers.Select(h => h.Key + ":" + h.Value + "\n")) + "\n" + signed + "\n" + payload;
            string scope = day + "/auto/s3/aws4_request";
            string toSign = "AWS4-HMAC-SHA256\n" + stamp + "\n" + scope + "\n" + PackProtocol.Hash(Encoding.UTF8.GetBytes(canonical));
            byte[] signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + settings.SecretKey), day), "auto"), "s3"), "aws4_request");
            string authorization = "AWS4-HMAC-SHA256 Credential=" + settings.AccessKey + "/" + scope + ", SignedHeaders=" + signed +
                ", Signature=" + PackProtocol.Hex(Hmac(signingKey, toSign));
            using var request = new HttpRequestMessage(method, "https://" + settings.Host + path + (query.Length == 0 ? "" : "?" + query));
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (cache != null) request.Headers.TryAddWithoutValidation("Cache-Control", cache);
            if (body != null || type != null) {
                request.Content = new ByteArrayContent(body ?? Array.Empty<byte>());
                if (type != null) request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            }
            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        }
        private static XElement ReadXml(HttpResponseMessage response) => XElement.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        private static byte[] Hmac(byte[] key, string value) {
            using var algorithm = new HMACSHA256(key);
            return algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
        }
        private static void Ensure(HttpResponseMessage response, string action) {
            if (!response.IsSuccessStatusCode) throw new IOException($"R2 {action}失败（HTTP {(int)response.StatusCode}）。请检查凭据、桶权限和网络。");
        }
        public void Dispose() => client.Dispose();
    }
}
