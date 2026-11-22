using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PCL.Modpack;

internal static partial class Program {
    private static void TestArchiveLinks() {
        Test("download timeout fires when a response stops sending data", _ => {
            using var stream = new StalledDownload();
            using var handler = new ArchiveRequests(_ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StreamContent(stream)
            });
            using var output = new MemoryStream();
            var snapshots = new List<PackTransferProgress>();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try {
                PackProtocol.Transfer("https://cdn.modrinth.com/test.jar", output, 2, null, null, 0, true, messageHandler: handler,
                    label: "Modrinth test.jar", report: snapshots.Add);
                throw new Exception("Stalled download completed without a timeout");
            } catch (IOException ex) {
                Assert(ex.Message == "下载线路长时间未返回数据。" && output.Length == 1 &&
                    elapsed.Elapsed >= TimeSpan.FromSeconds(14) && elapsed.Elapsed < TimeSpan.FromSeconds(23),
                    "Stalled response did not time out after 15 seconds");
            }
            Assert(snapshots[0].Text.Contains("等待服务器响应") && snapshots[0].Fraction == null,
                "Missing connection status before receiving headers");
            Assert(snapshots.Any(s => s.Fraction == 0.5 && s.Text.Contains("1 B / 2 B") && s.Text.Contains("1 B/s")),
                "Missing received size, fraction or speed");
            Assert(snapshots.Any(s => s.Fraction == 0.5 && s.Text.Contains("0 B/s") && s.Text.Contains("未收到数据")),
                "Progress stopped reporting while the connection was idle");
            Assert(snapshots.Last().TimedOut && snapshots.Last().Fraction == 0.5, "Missing timeout status");
        });
        Test("manual switch and cancellation interrupt a stalled response", _ => {
            foreach (bool cancel in new[] { false, true }) {
                var control = new PackDownloadControl();
                using var stream = new StalledDownload();
                using var handler = new ArchiveRequests(_ => new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StreamContent(stream)
                });
                using var output = new MemoryStream();
                var snapshots = new List<PackTransferProgress>();
                var worker = Task.Run(() => {
                    try {
                        PackProtocol.Transfer("https://example.org/test.jar", output, 2, null, null, 0, true,
                            messageHandler: handler, report: snapshots.Add, control: control, canSwitch: true);
                        return null;
                    } catch (Exception ex) {
                        return ex;
                    }
                });
                Assert(stream.WaitForStall() && control.CanSwitch, "The download did not reach the stalled read");
                Assert(cancel ? control.TryCancel() : control.TrySwitch(), "The interruption request was rejected");
                Assert(worker.Wait(TimeSpan.FromSeconds(5)), "The request did not interrupt the read");
                Assert(cancel ? worker.Result is PackUpdateCanceledException : worker.Result is IOException,
                    "The interruption did not return the expected outcome");
                Assert(!snapshots.Any(snapshot => snapshot.TimedOut) && !control.CanSwitch, "Manual interruption was reported as a timeout");
                if (cancel) continue;
                using var next = new ArchiveRequests(_ => new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new ByteArrayContent(new byte[] { 3, 4 })
                });
                output.SetLength(0);
                PackProtocol.Transfer("https://mirror.example/test.jar", output, 2, null, null, 0, true, messageHandler: next, control: control);
                Assert(output.ToArray().SequenceEqual(new byte[] { 3, 4 }), "The switched request interrupted the next route");
            }
        });
        Test("ongoing low throughput reports an estimated remaining time", _ => {
            var snapshots = new List<PackTransferProgress>();
            using (var reporter = new PackTransferReporter("slow download", 512 * 1024, TimeSpan.FromSeconds(15), snapshots.Add)) {
                reporter.BeginBody();
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                long received = 0;
                while (elapsed.Elapsed < TimeSpan.FromSeconds(7)) {
                    reporter.Receive(received += 4096);
                    Thread.Sleep(300);
                }
            }
            Assert(snapshots.Any(snapshot => snapshot.Text.Contains("仍在下载，预计剩余")), "Slow data was not accompanied by an estimate");
            Assert(!snapshots.Any(snapshot => snapshot.TimedOut), "Low throughput should keep the transfer running");
        });
        Test("ZIP ranges reuse the presigned URL and refresh once on S3 rejection", _ => {
            const string signer = "https://sign.example/pcl/archives/test.zip";
            var requests = new List<string>();
            int signatures = 0;
            bool reject = false;
            using var handler = new ArchiveRequests(request => {
                if (request.RequestUri!.Host == "sign.example") {
                    requests.Add("sign");
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = SignedAddress(DateTimeOffset.UtcNow.AddSeconds(++signatures), 900);
                    return response;
                }
                var range = request.Headers.Range!.Ranges.Single();
                requests.Add("range " + range.From);
                if (reject) {
                    reject = false;
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[] { 0x50, 0x4b }) };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From!.Value, range.To!.Value, 100);
                return partial;
            });
            var link = new PackArchiveLink();
            void Fetch(long offset) {
                using var output = new MemoryStream();
                PackProtocol.Transfer(signer, output, 2, null, offset, 100, true, link, handler);
                Assert(output.ToArray().SequenceEqual(new byte[] { 0x50, 0x4b }), "Wrong range body");
            }
            Fetch(0);
            Fetch(2);
            reject = true;
            Fetch(4);
            Fetch(6);
            Assert(requests.SequenceEqual(new[] { "sign", "range 0", "range 2", "range 4", "sign", "range 4", "range 6" }),
                "Unexpected signing requests or range retries");
        });
        Test("ZIP links renew before expiration and skip range-bound signatures", _ => {
            var link = new PackArchiveLink();
            var time = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
            link.Remember(SignedAddress(time, 900), time);
            Assert(link.Get(time.AddSeconds(869)) != null, "Link should still be reusable");
            Assert(link.Get(time.AddSeconds(870)) == null, "Link is inside the renewal margin");
            link.Remember(SignedAddress(time, 900, "host%3Brange"), time);
            Assert(link.Get(time) == null, "Range-bound signature should never be reused");
        });
    }

    private static Uri SignedAddress(DateTimeOffset time, int expires, string headers = "host") => new(
        "https://storage.example/bucket/pack.zip?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
        "&X-Amz-Date=" + time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) +
        "&X-Amz-Expires=" + expires + "&X-Amz-SignedHeaders=" + headers + "&X-Amz-Signature=" + new string('a', 64));

    private sealed class ArchiveRequests(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class StalledDownload() : MemoryStream(new byte[] { 1 }) {
        private readonly TaskCompletionSource<bool> disposed = new();
        private readonly TaskCompletionSource<bool> stalled = new();

        internal bool WaitForStall() => stalled.Task.Wait(TimeSpan.FromSeconds(5));

        public override int Read(byte[] buffer, int offset, int count) {
            int received = base.Read(buffer, offset, count);
            if (received > 0) return received;
            stalled.TrySetResult(true);
            if (!disposed.Task.Wait(TimeSpan.FromSeconds(25))) throw new IOException("Test stream was never aborted");
            throw new ObjectDisposedException(nameof(StalledDownload));
        }

        protected override void Dispose(bool disposing) {
            if (disposing) disposed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }
}
