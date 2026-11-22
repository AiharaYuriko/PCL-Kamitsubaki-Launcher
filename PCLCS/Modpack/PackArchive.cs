using System.IO;
using System.IO.Compression;

namespace PCL.Modpack;

internal sealed class PackArchive(JObject archive, string cache, Action<string> progress, Func<long, bool>? approveWhole,
    Action<string, Stream, long, long, long>? range = null, PackMirror? mirror = null,
    Action<PackTransferProgress>? reportTransfer = null, PackDownloadControl? control = null) : IDisposable {
    private readonly string url = (string)archive["download_url"]!;
    private readonly long size = (long)archive["size"]!;
    private readonly string hash = (string)archive["sha256"]!;
    private readonly PackArchiveLink mirrorLink = new();
    private bool? allowWhole;
    private bool verifiedWhole;
    private ZipArchive? whole;
    private ILookup<string, ZipArchiveEntry>? entriesByName;

    public void Dispose() => whole?.Dispose();

    private static string SourceName(string address, bool viaMirror) =>
        viaMirror ? "S3 备用线路" : $"主线路（{new Uri(address).Host}）";

    internal void ExtractRange(JObject file, string destination, string scratch, string? name = null, bool moreRoutes = false) {
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
        int route = 0;
        bool ReadEntry(string address, bool viaMirror) {
            bool canSwitch = (mirror != null && route++ == 0) || moreRoutes;
            string label = $"正在从 {SourceName(address, viaMirror)} 读取 ZIP 分段：{name ?? (string)file["path"]!}";
            progress(label);
            using var compressed = new FileStream(scratch, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            if (length != 0) {
                long offset = (long)entry["offset"]!;
                if (range != null) range(address, compressed, offset, length, size);
                else PackProtocol.DownloadRange(address, compressed, offset, length, size, true, viaMirror ? mirrorLink : null,
                    label, reportTransfer, control, canSwitch);
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
        } finally {
            if (System.IO.File.Exists(scratch)) System.IO.File.Delete(scratch);
        }
    }

    internal void FetchWhole(JObject file, string destination, Exception failure) {
        allowWhole ??= approveWhole?.Invoke(size) ?? false;
        if (allowWhole != true) throw new IOException("无法使用 ZIP 分段下载：" + failure.Message, failure);
        string temporary = cache + ".part";
        int round = 0;
        int route = 0;
        bool DownloadWhole(string address, bool viaMirror) {
            bool canSwitch = mirror != null && (route++ == 0 || round + 1 < PackUpdater.DownloadRounds);
            string label = $"正在从 {SourceName(address, viaMirror)} 下载完整 ZIP";
            if (round > 0) label += $"（第 {round + 1} 轮）";
            progress(label + "……");
            using (var output = System.IO.File.Create(temporary))
                PackProtocol.Download(address, output, size, label, reportTransfer, viaMirror ? mirrorLink : null, control, canSwitch);
            if (!Matches(temporary, size, hash)) throw new InvalidDataException("完整 ZIP 校验失败。");
            return true;
        }
        try {
            for (; ; round++) {
                route = 0;
                try {
                    if (mirror == null) DownloadWhole(url, false);
                    else mirror.Read(url, PackPublisher.ArchiveKey(hash), DownloadWhole);
                    break;
                } catch (Exception ex) when (round + 1 < PackUpdater.DownloadRounds && PackMirror.CanRetry(ex)) {
                    // Each line gets a turn before retrying a full download.
                }
            }
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
