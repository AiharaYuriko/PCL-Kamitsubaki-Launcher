using System.IO;
using System.Net.Http;

namespace PCL.Modpack;

/// <summary>Routes published objects through an optional HTTPS presigning service.</summary>
public sealed class PackMirror {
    private readonly string root;
    private bool useMirror;

    public string Address => root;
    public bool PreferMirror { get; }

    public PackMirror(string address, bool preferMirror = false) {
        PackProtocol.Https(address);
        var uri = new Uri(address);
        if (uri.Query.Length != 0) throw new InvalidDataException("备用地址应为签名服务的 HTTPS 根地址。");
        root = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        useMirror = PreferMirror = preferMirror;
    }

    internal T Read<T>(string primary, string key, Func<string, bool, T> read) {
        string secondary = root + "/" + key;
        bool mirrorFirst = useMirror;
        try {
            return read(mirrorFirst ? secondary : primary, mirrorFirst);
        } catch (Exception first) when (CanRetry(first)) {
            try {
                T result = read(mirrorFirst ? primary : secondary, !mirrorFirst);
                useMirror = !mirrorFirst;
                return result;
            } catch (Exception second) when (CanRetry(second)) {
                var r2Failure = mirrorFirst ? second : first;
                throw new IOException("主更新源和 S3 备用线路均读取失败。主更新源：" + r2Failure.Message, r2Failure);
            }
        }
    }

    private static bool CanRetry(Exception ex) =>
        ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or JsonException;
}
