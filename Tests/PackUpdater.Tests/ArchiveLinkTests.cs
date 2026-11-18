using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PCL.Modpack;

internal static partial class Program {
    private static void TestArchiveLinks() {
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
}
