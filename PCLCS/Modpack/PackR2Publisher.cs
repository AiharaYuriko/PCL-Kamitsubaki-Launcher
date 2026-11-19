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

    internal Uri Validate() {
        ValidateBucketCredentials();
        PackProtocol.Https(PublicRoot);
        var address = new Uri(PublicRoot);
        if (address.Query.Length != 0 || !Regex.IsMatch(address.AbsolutePath, @"^/(?:[a-zA-Z0-9_-]+(?:/[a-zA-Z0-9_-]+)*/?)?\z"))
            throw new InvalidDataException("公开地址应为 R2 自定义域名，可加桶内目录。目录名只能包含英文字母、数字、连字符和下划线。");
        return address;
    }

    internal void ValidateBucketCredentials() {
        if (!Regex.IsMatch(Account, @"^[a-f0-9]{32}(?:\.(?:eu|us|fedramp))?$") ||
            !Regex.IsMatch(Bucket, @"^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$"))
            throw new InvalidDataException("请检查 R2 Account ID 和存储桶名称。");
        if (!Regex.IsMatch(AccessKey, "^[a-zA-Z0-9]+$") || string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidDataException("请填写 R2 S3 Access Key ID 和 Secret Access Key。");
    }
}

public sealed class PackS3MirrorSettings {
    public const string DefaultSourceAddress = PackMirror.DefaultAddress;
    public const string DefaultBucket = "21045-yrrt-174027";
    internal const string Host = "fgws3-ocloud.ihep.ac.cn";
    internal const string Prefix = "pcl-mirror";
    internal const string Region = "us-east-1";
    public string Bucket { get; set; } = DefaultBucket;
    public string SourceAddress { get; set; } = DefaultSourceAddress;
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    internal void Validate() {
        if (!Regex.IsMatch(Bucket, @"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z") || Bucket.Contains(".."))
            throw new InvalidDataException("请填写有效的备用 S3 存储桶名称。");
        PackProtocol.Https(SourceAddress);
        if (new Uri(SourceAddress).Query.Length != 0)
            throw new InvalidDataException("备用线路请填写签名服务地址。");
        if (!Regex.IsMatch(AccessKey, @"\A[a-zA-Z0-9]+\z") || string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidDataException("请填写私有 S3 备用桶的发布凭据。");
    }

    internal void VerifySigningTarget(Uri? target) {
        string expectedPath = $"/{Bucket}/{Prefix}/{PackProtocol.IndexPath}";
        if (target == null || !target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || target.Host != Host ||
            !target.IsDefaultPort || target.UserInfo.Length != 0 || target.Fragment.Length != 0 ||
            target.AbsolutePath != expectedPath || !Regex.IsMatch(target.Query, @"[?&]X-Amz-Signature=[a-f0-9]{64}(?:&|\z)"))
            throw new IOException("备用签名服务指向的存储桶或目录与发布设置不一致。");
    }
}

public sealed record PackPublishResult(string SourceAddress, bool SupportsRanges) {
    public string? MirrorError { get; init; }
}

public sealed class PackR2Publisher(PackPublisher publisher) {
    private const string IndexKey = PackProtocol.IndexPath;
    private const string MirrorLockKey = "pcl/mirror-index-lock.json";
    private const int ManifestLimit = 8 * 1024 * 1024;

    internal static string ObjectKey(Uri address, string key) => ObjectKey(address.AbsolutePath.Trim('/'), key);
    private static string ObjectKey(string prefix, string key) => prefix.Length == 0 ? key : prefix + "/" + key;

    private static S3Objects OpenBaselines(PackR2Settings settings, string packId) {
        PackPublisher.ValidateIdentifier(packId);
        settings.ValidateBucketCredentials();
        return new S3Objects(settings.Host, settings.Bucket, "pcl-baselines/" + packId, "auto",
            settings.AccessKey, settings.SecretKey, "R2 云端基线", true);
    }

    public static string BaselineScope(string account, string bucket, string packId) =>
        PackProtocol.Hash(Encoding.UTF8.GetBytes(account + "/" + bucket + "/" + packId)).Substring(0, 16);

    internal static JArray ReadBaselineIndex(byte[]? bytes) {
        if (bytes == null) return new JArray();
        if (PackProtocol.Parse(bytes)["baselines"] is not JArray entries)
            throw new InvalidDataException("云端基线目录格式无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries) {
            if (item is not JObject entry || entry["name"]?.Type != JTokenType.String || entry["sha256"]?.Type != JTokenType.String ||
                !PackPublisher.ValidBaselineName((string?)entry["name"]) ||
                !Regex.IsMatch((string)entry["sha256"]!, "\\A[0-9a-f]{64}\\z") || !names.Add((string)entry["name"]!))
                throw new InvalidDataException("云端基线目录包含无效或重复的记录。");
        }
        return entries;
    }

    public void UploadBaseline(PackR2Settings settings, string packId, string baselineId) {
        var document = publisher.BaselineDocument(baselineId);
        PackPublisher.ValidateBaselineDocument(document);
        byte[] bytes = JsonBytes(document);
        if (bytes.Length > ManifestLimit) throw new IOException("基线清单超过 8 MiB。");
        string hash = PackProtocol.Hash(bytes);
        string name = (string)document["name"]!;
        using var cloud = OpenBaselines(settings, packId);
        bool uploaded = false;
        for (int attempt = 0; attempt < 5; attempt++) {
            var previous = cloud.Read("index.json");
            var entries = ReadBaselineIndex(previous.Bytes);
            var match = entries.SingleOrDefault(e => string.Equals((string?)e["name"], name, StringComparison.OrdinalIgnoreCase));
            if (match != null) {
                if ((string?)match["sha256"] == hash) return;
                throw new IOException("云端已有不同内容的同名基线，请使用其他名称：" + name);
            }
            if (!uploaded) {
                string key = "snapshots/" + hash + ".json";
                if (!cloud.TryPutJson(key, bytes, null) && cloud.Read(key).Bytes?.SequenceEqual(bytes) != true)
                    throw new IOException("云端基线对象与本地清单不一致。");
                uploaded = true;
            }
            entries.Add(new JObject { ["name"] = name, ["sha256"] = hash });
            byte[] index = JsonBytes(new JObject { ["baselines"] = entries });
            if (index.Length > ManifestLimit) throw new IOException("云端基线目录超过 8 MiB。");
            if (cloud.TryPutJson("index.json", index, previous.ETag)) return;
        }
        throw new IOException("云端基线目录正在被其他发布端更新，请重试。");
    }

    public int SyncBaselines(PackR2Settings settings, string packId) {
        using var cloud = OpenBaselines(settings, packId);
        string scope = BaselineScope(settings.Account, settings.Bucket, packId);
        var entries = ReadBaselineIndex(cloud.Read("index.json").Bytes);
        foreach (var entry in entries) {
            string name = (string)entry["name"]!;
            string hash = (string)entry["sha256"]!;
            byte[]? bytes = cloud.Read("snapshots/" + hash + ".json").Bytes;
            if (bytes == null || PackProtocol.Hash(bytes) != hash)
                throw new IOException("云端基线缺失或校验失败：" + name);
            var document = PackProtocol.Parse(bytes);
            if ((string?)document["name"] != name) throw new InvalidDataException("云端基线名称与目录不一致：" + name);
            publisher.CacheCloudBaseline(document, scope, hash);
        }
        return entries.Count;
    }

    public PackPublishResult Publish(PackPublishDetails details, PackR2Settings settings, IReadOnlyList<PackPublishFile> files, Action<string> progress,
        PackS3MirrorSettings? mirror = null) {
        var address = settings.Validate();
        string publicRoot = address.GetLeftPart(UriPartial.Path).TrimEnd('/');
        PackPublisher.ValidateDetails(details);
        mirror?.Validate();
        using var r2 = new S3Objects(settings.Host, settings.Bucket, address.AbsolutePath.Trim('/'), "auto", settings.AccessKey, settings.SecretKey, "R2", true);
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
            var manifest = publisher.Build(details, files, sequence, local, publicRoot);
            byte[] bytes = JsonBytes(manifest);
            if (bytes.Length > ManifestLimit) throw new IOException("版本清单超过客户端的下载上限。");
            string manifestHash = PackProtocol.Hash(bytes);
            string manifestKey = PackPublisher.ManifestKey(details.PackId, details.Channel, details.ReleaseId);
            string manifestUrl = PackPublisher.PublicUrl(publicRoot, manifestKey);
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
            var pointer = new JObject { ["id"] = details.Channel, ["release_id"] = details.ReleaseId, ["sequence"] = sequence,
                ["manifest_url"] = manifestUrl, ["manifest_sha256"] = manifestHash };
            if (!retry) {
                pack["name"] = details.Name;
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
            var result = new PackPublishResult(publicRoot, supportsRanges);
            if (mirror != null) {
                try {
                    SyncMirror(r2, mirror, details, pointer, bytes, local, size, archiveHash, progress);
                } catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or
                    UnauthorizedAccessException or JsonException or System.Xml.XmlException) {
                    result = result with { MirrorError = ex.Message };
                }
            }
            return result;
        } finally {
            if (File.Exists(local)) File.Delete(local);
        }
    }

    private void SyncMirror(S3Objects r2, PackS3MirrorSettings settings, PackPublishDetails details, JObject pointer, byte[] manifest,
        string local, long size, string archiveHash, Action<string> progress) {
        string signedIndex = PackPublisher.PublicUrl(settings.SourceAddress, IndexKey);
        using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) }) {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Modpack/1.0");
            using var response = client.GetAsync(signedIndex, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.Found)
                throw new IOException($"备用签名入口未返回下载跳转（HTTP {(int)response.StatusCode}）。");
            settings.VerifySigningTarget(response.Headers.Location);
        }
        using var s3 = new S3Objects(PackS3MirrorSettings.Host, settings.Bucket, PackS3MirrorSettings.Prefix,
            PackS3MirrorSettings.Region, settings.AccessKey, settings.SecretKey, "备用 S3", false);
        string archiveKey = PackPublisher.ArchiveKey(archiveHash);
        string manifestKey = PackPublisher.ManifestKey(details.PackId, details.Channel, details.ReleaseId);
        string manifestHash = (string)pointer["manifest_sha256"]!;
        progress("R2 已发布，正在同步到私有 S3……");
        s3.PutArchive(archiveKey, local, size, archiveHash, progress);
        s3.PutImmutable(manifestKey, manifest);
        progress("正在验证备用线路的预签名下载……");
        if (PackProtocol.Hash(PackProtocol.Fetch(PackPublisher.PublicUrl(settings.SourceAddress, manifestKey), ManifestLimit, true)) != manifestHash)
            throw new IOException("备用线路返回的版本清单与 R2 不一致。");
        using var probe = new MemoryStream();
        PackProtocol.DownloadRange(PackPublisher.PublicUrl(settings.SourceAddress, archiveKey), probe, 0, 2, size, true, null);
        if (!probe.ToArray().SequenceEqual(new byte[] { 0x50, 0x4b })) throw new IOException("备用线路的 ZIP 分段校验失败。");

        // OSCA accepts conditional PUT headers without enforcing them. Serialize only the index update through R2.
        using var ownerFile = new FileStream(publisher.ProjectFile("mirror-lock.id"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string owner = ReadLockOwner(ownerFile);
        var previous = r2.Read(MirrorLockKey);
        string? activeOwner = previous.Bytes == null ? null : (string?)PackProtocol.Parse(previous.Bytes)["owner"];
        if (activeOwner != owner) {
            if (!string.IsNullOrEmpty(activeOwner)) throw new IOException("另一个发布项目正在切换备用频道，请稍后重试。若此前发布中断，请从原实例重试以恢复。");
            r2.PutIndex(MirrorLockKey, JsonBytes(new JObject { ["owner"] = owner }), previous.ETag);
        }
        try {
            if (!r2.PointsTo(IndexKey, details, manifestHash)) throw new IOException("R2 频道已改变，请在正式发布页核对当前发布后重试。");
            progress("正在切换备用 S3 频道……");
            var previousMirror = s3.Read(IndexKey).Bytes;
            var mirrorIndex = UpdateMirrorIndex(previousMirror, details.Name, details.PackId, pointer);
            byte[] indexBytes = JsonBytes(mirrorIndex);
            if (indexBytes.Length > ManifestLimit) throw new IOException("备用频道目录超过客户端的下载上限。");
            if (previousMirror?.SequenceEqual(indexBytes) != true) s3.PutIndex(IndexKey, indexBytes, null);
            if (!PackProtocol.Fetch(signedIndex, ManifestLimit, true).SequenceEqual(indexBytes))
                throw new IOException("预签名入口返回的频道目录与备用桶不同。");
        } finally {
            var locked = r2.Read(MirrorLockKey);
            if (locked.Bytes != null && (string?)PackProtocol.Parse(locked.Bytes)["owner"] == owner)
                r2.PutIndex(MirrorLockKey, JsonBytes(new JObject { ["owner"] = "" }), locked.ETag);
        }
    }

    private static string ReadLockOwner(FileStream file) {
        using var reader = new StreamReader(file, Encoding.UTF8, false, 128, leaveOpen: true);
        string? saved = reader.ReadLine();
        if (Guid.TryParseExact(saved, "N", out _)) return saved!;
        string owner = Guid.NewGuid().ToString("N");
        file.Position = 0;
        file.SetLength(0);
        byte[] bytes = Encoding.ASCII.GetBytes(owner);
        file.Write(bytes, 0, bytes.Length);
        file.Flush(flushToDisk: true);
        return owner;
    }

    internal static JObject UpdateMirrorIndex(byte[]? previous, string name, string packId, JObject pointer) {
        var index = previous == null ? new JObject { ["schema"] = "pcl.modpack.index.v1", ["name"] = name, ["packs"] = new JArray() }
            : PackProtocol.Parse(previous);
        PackProtocol.Validate(index, "index");
        var packs = (JArray)index["packs"]!;
        var pack = packs.OfType<JObject>().SingleOrDefault(item => (string?)item["id"] == packId);
        if (pack == null) {
            pack = new JObject { ["id"] = packId, ["name"] = name, ["channels"] = new JArray() };
            packs.Add(pack);
        }
        var channels = (JArray)pack["channels"]!;
        var channel = channels.OfType<JObject>().SingleOrDefault(item => (string?)item["id"] == (string?)pointer["id"]);
        if (channel != null) {
            long currentSequence = (long)channel["sequence"]!;
            long newSequence = (long)pointer["sequence"]!;
            if (currentSequence > newSequence || currentSequence == newSequence && !JToken.DeepEquals(channel, pointer))
                throw new IOException("备用频道已存在更新的版本或冲突的发布序号。");
            channel.Replace(pointer.DeepClone());
        } else channels.Add(pointer.DeepClone());
        pack["name"] = name;
        PackProtocol.Validate(index, "index");
        return index;
    }

    internal static byte[] JsonBytes(JObject value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));

    private sealed class S3Objects(string host, string bucket, string prefix, string region, string accessKey, string secretKey,
        string label, bool conditionalWrites) : IDisposable {
        private const int PartSize = 32 * 1024 * 1024;
        private string CacheForImmutable => conditionalWrites ? "public, max-age=31536000, immutable" : "private, no-store";
        private string? NewObjectCondition => conditionalWrites ? "if-none-match" : null;
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
                if (output.Length + read > ManifestLimit) throw new IOException(label + " 清单超过下载限制。");
                output.Write(buffer, 0, read);
            }
            return (output.ToArray(), response.Headers.ETag?.ToString() ?? throw new IOException(label + " 未返回清单 ETag。"));
        }
        internal void PutIndex(string key, byte[] bytes, string? etag) {
            if (!TryPutJson(key, bytes, etag, "切换频道"))
                throw new IOException("频道已被其他发布修改，请重新读取后重试。");
        }
        internal bool TryPutJson(string key, byte[] bytes, string? etag, string operation = "保存云端基线") {
            using var response = Send(HttpMethod.Put, key, body: bytes, type: "application/json", cache: "no-store",
                condition: conditionalWrites ? (etag == null ? "if-none-match" : "if-match") : null, conditionValue: etag ?? "*");
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                return false;
            Ensure(response, operation);
            return true;
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
            if (!conditionalWrites && Read(key).Bytes is byte[] previous) {
                if (previous.SequenceEqual(bytes)) return;
                throw new IOException("同一发布 ID 已有不同的备用清单，请使用新的发布 ID。");
            }
            using var response = Send(HttpMethod.Put, key, body: bytes, type: "application/json", cache: CacheForImmutable,
                condition: NewObjectCondition, conditionValue: "*");
            if (response.StatusCode == HttpStatusCode.PreconditionFailed) {
                if (Read(key).Bytes?.SequenceEqual(bytes) == true) return;
                throw new IOException("同一发布 ID 已有不同的清单，请使用新的发布 ID。");
            }
            Ensure(response, "上传版本清单");
        }

        internal void PutArchive(string key, string path, long size, string hash, Action<string> progress) {
            if (HasArchive(key, size, hash)) return;
            if (size <= PartSize) {
                using var put = Send(HttpMethod.Put, key, body: File.ReadAllBytes(path), type: "application/zip", cache: CacheForImmutable,
                    condition: NewObjectCondition, conditionValue: "*", metadata: hash);
                if (put.StatusCode == HttpStatusCode.PreconditionFailed && HasArchive(key, size, hash)) return;
                Ensure(put, "上传 ZIP");
                if (!conditionalWrites && !HasArchive(key, size, hash)) throw new IOException("备用 S3 未保存 ZIP。");
                return;
            }
            long partSize = Math.Max(PartSize, (size + 9999) / 10000);
            if (partSize > 512 * 1024 * 1024) throw new IOException("ZIP 超过当前上传器支持的大小。");
            using var create = Send(HttpMethod.Post, key, "uploads=", type: "application/zip", cache: CacheForImmutable,
                metadata: hash, condition: NewObjectCondition, conditionValue: "*");
            if (create.StatusCode == HttpStatusCode.PreconditionFailed && HasArchive(key, size, hash)) return;
            Ensure(create, "初始化分片上传");
            string id = ReadXml(create).Elements().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value
                ?? throw new IOException(label + " 未返回分片上传 ID。");
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
                    "application/xml", condition: NewObjectCondition, conditionValue: "*");
                if (commit.StatusCode == HttpStatusCode.PreconditionFailed && HasArchive(key, size, hash)) return;
                Ensure(commit, "提交 ZIP 分片");
                if (ReadXml(commit).Name.LocalName != "CompleteMultipartUploadResult") throw new IOException(label + " 未确认 ZIP 分片提交。");
                completed = true;
                if (!conditionalWrites && !HasArchive(key, size, hash)) throw new IOException("备用 S3 未保存 ZIP。");
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
                    return put.Headers.ETag?.ToString() ?? throw new IOException(label + " 未返回分片 ETag。");
                } catch (HttpRequestException) when (attempt < 2) { }
                catch (OperationCanceledException) when (attempt < 2) { }
            }
        }

        private bool HasArchive(string key, long size, string hash) {
            using var response = Send(Head, key);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            Ensure(response, "验证 ZIP 对象");
            if (response.Content.Headers.ContentLength != size || !response.Headers.TryGetValues("x-amz-meta-sha256", out var checksums) || !checksums.Contains(hash))
                throw new IOException(label + " 已有同名但校验信息不同的 ZIP。");
            return true;
        }

        private HttpResponseMessage Send(HttpMethod method, string key, string query = "", byte[]? body = null, string? type = null,
            string? cache = null, string? condition = null, string? conditionValue = null, string? metadata = null) {
            DateTime time = DateTime.UtcNow;
            string day = time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string stamp = time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string payload = PackProtocol.Hash(body ?? Array.Empty<byte>());
            string path = "/" + bucket + "/" + string.Join("/", ObjectKey(prefix, key).Split('/').Select(Uri.EscapeDataString));
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) {
                ["host"] = host, ["x-amz-content-sha256"] = payload, ["x-amz-date"] = stamp
            };
            if (condition != null) headers[condition] = conditionValue!;
            if (metadata != null) headers["x-amz-meta-sha256"] = metadata;
            string signed = string.Join(";", headers.Keys);
            string canonical = method.Method + "\n" + path + "\n" + query + "\n" +
                string.Concat(headers.Select(h => h.Key + ":" + h.Value + "\n")) + "\n" + signed + "\n" + payload;
            string scope = day + "/" + region + "/s3/aws4_request";
            string toSign = "AWS4-HMAC-SHA256\n" + stamp + "\n" + scope + "\n" + PackProtocol.Hash(Encoding.UTF8.GetBytes(canonical));
            byte[] signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day), region), "s3"), "aws4_request");
            string authorization = "AWS4-HMAC-SHA256 Credential=" + accessKey + "/" + scope + ", SignedHeaders=" + signed +
                ", Signature=" + PackProtocol.Hex(Hmac(signingKey, toSign));
            using var request = new HttpRequestMessage(method, "https://" + host + path + (query.Length == 0 ? "" : "?" + query));
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
        private void Ensure(HttpResponseMessage response, string action) {
            if (!response.IsSuccessStatusCode) throw new IOException($"{label} {action}失败（HTTP {(int)response.StatusCode}）。请检查凭据、桶权限和网络。");
        }
        public void Dispose() => client.Dispose();
    }
}
