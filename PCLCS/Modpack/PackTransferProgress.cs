using System.Diagnostics;

namespace PCL.Modpack;

public sealed record PackTransferProgress(string Text, double? Fraction, bool TimedOut = false);

internal sealed class PackTransferReporter : IDisposable {
    internal const int SlowBytesPerSecond = 64 * 1024;
    internal static readonly TimeSpan SlowDuration = TimeSpan.FromSeconds(5);
    private readonly string label;
    private readonly long length;
    private readonly TimeSpan idleTimeout;
    private readonly Action<PackTransferProgress> report;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private readonly Timer timer;
    private long received, sampledBytes;
    private TimeSpan lastActivity, lastSample;
    private TimeSpan? slowSince;
    private double averageSpeed;
    private bool receiving, stopped, disposed;

    internal PackTransferReporter(string label, long length, TimeSpan idleTimeout, Action<PackTransferProgress> report) {
        this.label = label;
        this.length = length;
        this.idleTimeout = idleTimeout;
        this.report = report;
        Publish();
        var interval = TimeSpan.FromSeconds(1);
        timer = new Timer(_ => Publish(), null, interval, interval);
    }

    internal void BeginRequest() {
        lock (gate) lastActivity = clock.Elapsed;
    }

    internal void BeginBody() {
        lock (gate) {
            receiving = true;
            lastActivity = lastSample = clock.Elapsed;
            Publish();
        }
    }

    internal void Receive(long bytes) {
        lock (gate) {
            received = bytes;
            lastActivity = clock.Elapsed;
        }
    }

    private double? Fraction => receiving && length > 0 ? Math.Min(1, (double)received / length) : null;

    private string Size => $"{FormatSize(received)} / {FormatSize(length)}";

    private void Publish() {
        lock (gate) {
            if (stopped || disposed) return;
            var now = clock.Elapsed;
            var idle = now - lastActivity;
            double elapsed = Math.Max(0, (now - lastSample).TotalSeconds);
            double speed = elapsed > 0 ? (received - sampledBytes) / elapsed : 0;
            if (!receiving) {
                report(new PackTransferProgress($"{label} · 等待服务器响应 {WaitTime(idle)}", null));
            } else {
                string waiting = idle.TotalSeconds >= 1 ? $" · 未收到数据 {WaitTime(idle)}" : "";
                if (idle.TotalSeconds < 2 && received > 0) {
                    averageSpeed = averageSpeed == 0 ? speed : averageSpeed * 0.65 + speed * 0.35;
                    slowSince = averageSpeed > 0 && averageSpeed < SlowBytesPerSecond ? slowSince ?? lastSample : null;
                } else {
                    averageSpeed = 0;
                    slowSince = null;
                }
                string estimate = slowSince is { } start && now - start >= SlowDuration && received < length
                    ? $" · 仍在下载，预计剩余 {FormatRemaining((length - received) / averageSpeed)}"
                    : "";
                report(new PackTransferProgress($"{label} · {Size} · {FormatSize(speed)}/s{waiting}{estimate}", Fraction));
            }
            lastSample = now;
            sampledBytes = received;
        }
    }

    private string WaitTime(TimeSpan elapsed) =>
        $"{Math.Min((int)elapsed.TotalSeconds, (int)idleTimeout.TotalSeconds)}/{idleTimeout.TotalSeconds:0} 秒";

    internal void Complete() {
        lock (gate) {
            stopped = true;
            report(new PackTransferProgress($"{label} · 已接收 {Size}，正在校验", Fraction));
        }
    }

    internal void Timeout() {
        lock (gate) {
            stopped = true;
            string awaited = receiving ? "数据" : "服务器响应";
            report(new PackTransferProgress($"{label} · {idleTimeout.TotalSeconds:0} 秒未收到{awaited}，本次下载超时", Fraction, true));
        }
    }

    private static string FormatSize(double bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):0.00} MiB" :
        bytes >= 1024 ? $"{bytes / 1024:0.0} KiB" : $"{bytes:0} B";

    private static string FormatRemaining(double seconds) {
        if (seconds > 7 * 86400) return "超过 7 天";
        var duration = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (duration.Days > 0) return $"{duration.Days} 天 {duration.Hours} 小时";
        if (duration.Hours > 0) return $"{duration.Hours} 小时 {duration.Minutes} 分";
        if (duration.Minutes > 0) return $"{duration.Minutes} 分 {duration.Seconds} 秒";
        return $"{duration.Seconds} 秒";
    }

    public void Dispose() {
        lock (gate) disposed = true;
        timer.Dispose();
    }
}
