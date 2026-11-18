using System.IO;
using System.IO.Compression;

namespace PCL.Modpack;

internal sealed class PackArchive(JObject archive, string cache, Action<string> progress, Func<long, bool>? approveWhole,
    Action<string, Stream, long, long, long>? range = null, PackMirror? mirror = null) : IDisposable {
    private readonly string url = (string)archive["download_url"]!;
    private readonly long size = (long)archive["size"]!;
    private readonly string hash = (string)archive["sha256"]!;
    private readonly PackArchiveLink mirrorLink = new();
    private bool? allowWhole;
    private bool verifiedWhole;
    private ZipArchive? whole;
    private ILookup<string, ZipArchiveEntry>? entriesByName;

    public void Dispose() => whole?.Dispose();

    internal void Extract(JObject file, string destination, string scratch) {
        if (file["entry"] is not JObject entry) throw new IOException("此文件没有 ZIP 备用内容。");
        if (!verifiedWhole && System.IO.File.Exists(cache)) {
            verifiedWhole = Matches(cache, size, hash);
            if (!verifiedWhole) System.IO.File.Delete(cache);
        }
        if (verifiedWhole) {
            ExtractWhole(file, destination);
            return;
        }
        long length = (long)entry["length"]!;
        bool ReadEntry(string address, bool viaMirror) {
            if (viaMirror) progress("正在从 S3 备用线路读取：" + (string)file["path"]!);
            using var compressed = new FileStream(scratch, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            if (length != 0) {
                long offset = (long)entry["offset"]!;
                if (range != null) range(address, compressed, offset, length, size);
                else PackProtocol.DownloadRange(address, compressed, offset, length, size, mirror != null, viaMirror ? mirrorLink : null);
            }
            compressed.Position = 0;
            long expected = (long)file["size"]!;
            using (var output = System.IO.File.Create(destination)) {
                if ((string?)entry["method"] == "deflate" && length != 0) {
                    using var inflate = new DeflateStream(compressed, CompressionMode.Decompress, true);
                    CopyBounded(inflate, output, expected);
                } else CopyBounded(compressed, output, expected);
            }
            if (!Matches(destination, expected, (string)file["sha256"]!))
                throw new InvalidDataException("ZIP 分段文件的 SHA-256 不匹配。");
            return true;
        }
        try {
            if (mirror == null) ReadEntry(url, false);
            else mirror.Read(url, PackPublisher.ArchiveKey(hash), ReadEntry);
        } catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Http.HttpRequestException or OperationCanceledException) {
            FetchWhole(file, destination, ex);
        } finally {
            if (System.IO.File.Exists(scratch)) System.IO.File.Delete(scratch);
        }
    }

    private void FetchWhole(JObject file, string destination, Exception failure) {
        allowWhole ??= approveWhole?.Invoke(size) ?? false;
        if (allowWhole != true) throw new IOException("无法使用 ZIP 分段下载：" + failure.Message, failure);
        string temporary = cache + ".part";
        bool DownloadWhole(string address, bool viaMirror) {
            string label = viaMirror ? "正在从 S3 备用线路下载完整整合包" : "正在下载完整整合包";
            progress(label + "……");
            using (var output = System.IO.File.Create(temporary)) PackProtocol.Download(address, output, size,
                received => progress($"{label}：{received / 1048576d:F1} / {size / 1048576d:F1} MB"), mirror != null, viaMirror ? mirrorLink : null);
            if (!Matches(temporary, size, hash)) throw new InvalidDataException("完整 ZIP 校验失败。");
            return true;
        }
        try {
            if (mirror == null) DownloadWhole(url, false);
            else mirror.Read(url, PackPublisher.ArchiveKey(hash), DownloadWhole);
            System.IO.File.Move(temporary, cache);
            verifiedWhole = true;
        } finally {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
        ExtractWhole(file, destination);
    }

    private void ExtractWhole(JObject file, string destination) {
        string name = "overrides/" + (string)file["path"]!;
        whole ??= ZipFile.OpenRead(cache);
        entriesByName ??= whole.Entries.ToLookup(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        var entries = entriesByName[name];
        long expectedSize = (long)file["size"]!;
        if (entries.Count() != 1 || entries.First().Length != expectedSize) throw new InvalidDataException("完整 ZIP 中的条目不符：" + name);
        using var input = entries.First().Open();
        using var output = System.IO.File.Create(destination);
        CopyBounded(input, output, expectedSize);
    }

    internal static bool Matches(string file, long size, string sha256) =>
        System.IO.File.Exists(file) && new FileInfo(file).Length == size && PackProtocol.HashFile(file) == sha256;

    internal static void CopyBounded(Stream input, Stream output, long expected) {
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0) {
            if (read > expected - received) throw new InvalidDataException("文件超过清单声明的大小。");
            output.Write(buffer, 0, read);
            received += read;
        }
        if (received != expected) throw new EndOfStreamException("文件小于清单声明的大小。");
    }
}
