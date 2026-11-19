using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml.Linq;
using PCL.Modpack;

internal static partial class Program {
    private static void TestMultipartUpload() {
        Test("ZIP parts upload concurrently, retry individually and commit in order", root => {
            const int partSize = 32 * 1024 * 1024;
            const long size = 3L * partSize + 1;
            string path = Path.Combine(root, "archive.zip");
            using (var fixture = File.Create(path)) {
                for (int index = 0; index < 4; index++) {
                    fixture.Position = index * (long)partSize;
                    fixture.WriteByte((byte)(index + 1));
                }
            }
            var attempts = new ConcurrentDictionary<int, int>();
            var allPartsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0;
            XElement? completion = null;
            using var storage = new PackR2Publisher.S3Objects("r2.example", "packs", "", "auto", "access", "secret", "R2", true,
                new MultipartRequests(async (request, cancellation) => {
                    string query = request.RequestUri!.Query;
                    if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (query == "?uploads=") return XmlResponse("<InitiateMultipartUploadResult><UploadId>test</UploadId></InitiateMultipartUploadResult>");
                    if (request.Method == HttpMethod.Post) {
                        Assert(Volatile.Read(ref active) == 0, "Completion started while parts were still uploading");
                        completion = XElement.Parse(await request.Content.ReadAsStringAsync());
                        return XmlResponse("<CompleteMultipartUploadResult/>");
                    }

                    Assert(request.Method == HttpMethod.Put, "Unexpected multipart request");
                    int number = int.Parse(query.Split('&')[0].Substring("?partNumber=".Length));
                    int attempt = attempts.AddOrUpdate(number, 1, (_, previous) => previous + 1);
                    if (Interlocked.Increment(ref active) == 4) allPartsStarted.TrySetResult(true);
                    try {
                        var ready = await Task.WhenAny(allPartsStarted.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellation));
                        Assert(ready == allPartsStarted.Task, "Expected four concurrent part requests");
                        using var body = await request.Content.ReadAsStreamAsync();
                        Assert(body.Length == (number == 4 ? 1 : partSize) && body.ReadByte() == number, "Incorrect part payload");
                        if (number == 2 && attempt == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new EntityTagHeaderValue($"\"part-{number}\"") } };
                    } finally {
                        Interlocked.Decrement(ref active);
                    }
                }));
            var progress = new List<string>();
            storage.PutArchive("archive.zip", path, size, new string('a', 64), progress.Add);
            Assert(attempts.OrderBy(part => part.Key).Select(part => part.Value).SequenceEqual(new[] { 1, 2, 1, 1 }), "Only the rejected part should retry");
            Assert(completion!.Elements("Part").Select(part => $"{part.Element("PartNumber")!.Value}:{part.Element("ETag")!.Value}")
                .SequenceEqual(Enumerable.Range(1, 4).Select(number => $"{number}:\"part-{number}\"")), "Commit must use sorted part numbers and their returned ETags");
            Assert(progress.Count == 5 && progress.Select((message, count) => message.Contains($"{count}/4 片")).All(value => value),
                "Progress must count acknowledged parts once");
        });
    }

    private static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml) };

    private sealed class MultipartRequests(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
