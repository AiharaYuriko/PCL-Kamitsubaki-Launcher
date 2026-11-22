using System.IO;

namespace PCL.Modpack;

public sealed class PackUpdateCanceledException() : OperationCanceledException("已取消整合包更新。");

public sealed class PackDownloadControl {
    private readonly object gate = new();
    private Request? active;
    private bool canceled, applying;

    public bool IsCancellationRequested { get { lock (gate) return canceled; } }
    public bool CanCancel { get { lock (gate) return !canceled && !applying; } }
    public bool CanSwitch { get { lock (gate) return !canceled && active is { CanSwitch: true, Switched: false }; } }

    public bool TryCancel() {
        lock (gate) {
            if (canceled || applying) return false;
            canceled = true;
            active?.Interrupt();
            return true;
        }
    }

    public bool TrySwitch() {
        lock (gate) {
            if (canceled || active is not { CanSwitch: true, Switched: false } request) return false;
            request.Switched = true;
            request.Interrupt();
            return true;
        }
    }

    public void ThrowIfCanceled() {
        if (IsCancellationRequested) throw new PackUpdateCanceledException();
    }

    internal void BeginApply() {
        lock (gate) {
            ThrowIfCanceled();
            applying = true;
        }
    }

    internal Request BeginRequest(bool canSwitch) {
        lock (gate) {
            ThrowIfCanceled();
            return active = new Request(this, canSwitch);
        }
    }

    internal sealed class Request(PackDownloadControl owner, bool canSwitch) : IDisposable {
        private readonly CancellationTokenSource abort = new();
        private readonly object abortGate = new();
        private bool disposed;
        internal bool CanSwitch => canSwitch;
        internal bool Switched { get; set; }
        internal CancellationToken Token => abort.Token;

        internal void Interrupt() => ThreadPool.QueueUserWorkItem(_ => {
            lock (abortGate) {
                if (!disposed) abort.Cancel();
            }
        });

        internal void Check(bool finished = false) {
            lock (owner.gate) {
                owner.ThrowIfCanceled();
                if (Switched) throw new IOException("已跳过当前下载线路。");
                if (finished) owner.active = null;
            }
        }

        public void Dispose() {
            lock (owner.gate) {
                if (ReferenceEquals(owner.active, this)) owner.active = null;
            }
            lock (abortGate) {
                disposed = true;
                abort.Dispose();
            }
        }
    }
}
