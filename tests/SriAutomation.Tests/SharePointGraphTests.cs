using System.Net;
using System.Text;
using SriAutomation.Infrastructure;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public class SharePointGraphTests
{
    [Fact]
    public async Task ExistingIdenticalFileIsNotOverwrittenAndDifferentFileIsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes("fixture");
        var handler = new GraphHandler(bytes);
        var graph = new SharePointGraphClient(new HttpClient(handler), new("tenant", "client", "test-only", "host,site,web", "drive", "root"));
        await graph.Upload("root/1790000000001/2026/09/Facturas/a.xml", bytes, DownloadPipeline.Hash(bytes), CancellationToken.None);
        Assert.DoesNotContain(handler.Methods, method => method == HttpMethod.Put);
        var ex = await Assert.ThrowsAsync<SharePointGraphException>(() => graph.Upload("root/1790000000001/2026/09/Facturas/a.xml", Encoding.UTF8.GetBytes("different"), DownloadPipeline.Hash(Encoding.UTF8.GetBytes("different")), CancellationToken.None));
        Assert.False(ex.Transient);
        Assert.DoesNotContain(handler.Methods, method => method == HttpMethod.Put);
    }

    [Fact]
    public async Task ThrottlingPreservesRetryAfter()
    {
        var handler = new GraphHandler([]) { Throttle = true };
        var graph = new SharePointGraphClient(new HttpClient(handler), new("tenant", "client", "test-only", "host,site,web", "drive", "root"));
        var ex = await Assert.ThrowsAsync<SharePointGraphException>(() => graph.Upload("root/a.xml", [1], DownloadPipeline.Hash([1]), CancellationToken.None));
        Assert.True(ex.Transient);
        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
    }

    [Fact]
    public async Task QueuePersistsSuccessBeforeRemovingTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sri-sharepoint-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("fixture");
            File.WriteAllBytes(Path.Combine(directory, "voucher.xml"), bytes);
            var state = new State();
            state.SharePointUploads.Add(new SharePointUpload { DocumentId = "doc", Account = "account", User = "admin", TaxId = "1790000000001", Extension = "xml", LocalPath = "voucher.xml", RemotePath = "root/voucher.xml", Sha256 = DownloadPipeline.Hash(bytes) });
            var repository = new MemoryRepository(state);
            var graph = new SharePointGraphClient(new HttpClient(new GraphHandler(bytes)), new("tenant", "client", "test-only", "host,site,web", "drive", "root"));
            var processor = new SharePointUploadProcessor(repository, new DocumentStorage(directory), graph);
            Assert.True(await processor.Tick(CancellationToken.None));
            Assert.Equal("Completed", state.SharePointUploads.Single().Status);
            Assert.True(File.Exists(Path.Combine(directory, "voucher.xml")));
            Assert.True(await processor.Tick(CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "voucher.xml")));
            Assert.Contains(state.Audit, a => a.Result.Contains("Confirmada"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ThrottledQueueRetainsLocalFileAndSchedulesRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sri-sharepoint-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(Path.Combine(directory, "voucher.xml"), bytes);
            var state = new State();
            state.SharePointUploads.Add(new SharePointUpload { DocumentId = "doc", Account = "account", User = "admin", LocalPath = "voucher.xml", RemotePath = "root/voucher.xml", Sha256 = DownloadPipeline.Hash(bytes) });
            var graph = new SharePointGraphClient(new HttpClient(new GraphHandler([]) { Throttle = true }), new("tenant", "client", "test-only", "host,site,web", "drive", "root"));
            var before = DateTimeOffset.UtcNow;
            await new SharePointUploadProcessor(new MemoryRepository(state), new DocumentStorage(directory), graph).Tick(CancellationToken.None);
            var item = state.SharePointUploads.Single();
            Assert.Equal("Pending", item.Status);
            Assert.True(item.NextAttempt >= before.AddSeconds(17));
            Assert.True(File.Exists(Path.Combine(directory, "voucher.xml")));
            Assert.Contains(state.Audit, a => a.Result.Contains("Reintento pendiente"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UploadSessionCreatesAndVerifiesNewFile()
    {
        var handler = new UploadHandler();
        var graph = new SharePointGraphClient(new HttpClient(handler), new("tenant", "client", "test-only", "host,site,web", "drive", "root"));
        var bytes = Encoding.UTF8.GetBytes("new fixture");
        await graph.Upload("root/voucher.xml", bytes, DownloadPipeline.Hash(bytes), CancellationToken.None);
        Assert.Equal(bytes, handler.Uploaded);
        Assert.True(handler.FailOnConflict);
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        public byte[]? Uploaded { get; private set; }
        public bool FailOnConflict { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "login.microsoftonline.com") return Json("{\"access_token\":\"test-token\",\"expires_in\":3600}");
            if (request.RequestUri.Host == "upload.example") { Uploaded = await request.Content!.ReadAsByteArrayAsync(cancellationToken); return Json("{}"); }
            if (path.EndsWith("/content")) return Uploaded is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Uploaded) };
            if (path.EndsWith("/createUploadSession"))
            {
                FailOnConflict = (await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("\"fail\"");
                return Json("{\"uploadUrl\":\"https://upload.example/session\"}");
            }
            return Json("{\"id\":\"drive\",\"sharepointIds\":{\"siteId\":\"site\",\"webId\":\"web\"}}");
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    private sealed class MemoryRepository(State state) : IStateRepository
    {
        public T Read<T>(Func<State, T> read) => read(state);
        public T Update<T>(Func<State, T> mutate) => mutate(state);
    }

    private sealed class GraphHandler(byte[] content) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public bool Throttle { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            if (request.RequestUri!.Host == "login.microsoftonline.com") return Task.FromResult(Json("{\"access_token\":\"test-token\",\"expires_in\":3600}"));
            if (Throttle)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
                return Task.FromResult(response);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/content")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            return Task.FromResult(Json("{\"id\":\"drive\",\"sharepointIds\":{\"siteId\":\"site\",\"webId\":\"web\"}}"));
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
