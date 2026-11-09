using System.Net;
using System.Net.Http.Json;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public sealed class PendingTransferQueueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sri-pending-" + Guid.NewGuid().ToString("N"));
    private const string Key = "1111111111111111111111111111111111111111111111111";
    private static readonly DateOnly Date = new(2026, 1, 2);
    private static readonly byte[] Xml = [1, 2, 3];
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private PendingTransferQueue Queue(long limit = 1000000) => new(root, limit);
    private void Save(PendingTransferQueue q, string extension = "xml", byte[]? bytes = null) => q.Save("account", "Facturas", Key, Date, extension, bytes ?? Xml, "job", 1);

    [Fact] public void SurvivesRestartAndFormatsAreIndependent()
    {
        Save(Queue()); Save(Queue(), "pdf", [4, 5]);
        Assert.Equal(Xml, Queue().Load("account", "Facturas", Key, Date, "xml"));
        Assert.Equal(new byte[] { 4, 5 }, Queue().Load("account", "Facturas", Key, Date, "pdf"));
    }
    [Fact] public void WrongHashIsRetainedAndNeverSent()
    {
        Save(Queue());
        var content = Directory.GetFiles(root, "content", SearchOption.AllDirectories).Single(); File.WriteAllBytes(content, [9]);
        Assert.Throws<IOException>(() => Queue().Load("account", "Facturas", Key, Date, "xml"));
        Assert.True(File.Exists(content));
    }
    [Fact] public void FullQueueFailsExplicitlyAndPreservesExistingItems()
    {
        Save(Queue());
        Assert.Throws<IOException>(() => Save(Queue(1), "pdf"));
        Assert.Equal(Xml, Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Fact] public void ConfirmationMustMatchBeforeRemoval()
    {
        var q = Queue(); Save(q);
        Assert.Throws<IOException>(() => q.Confirm("account", "Facturas", Key, Date, "xml", new string('0', 64)));
        Assert.NotNull(q.Load("account", "Facturas", Key, Date, "xml"));
        q.Confirm("account", "Facturas", Key, Date, "xml", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Xml)));
        Assert.Null(q.Load("account", "Facturas", Key, Date, "xml"));
    }
    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public void RejectedLeaseOrCredentialsKeepFile(HttpStatusCode code)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(code))) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: Queue());
        Assert.Throws<HttpRequestException>(() => remote.Process(new("account", "Facturas", Key, Date, Xml, null)));
        Assert.Equal(Xml, Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Fact] public void SuccessfulResponseRemovesConfirmedFile()
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Xml));
        var voucher = new Voucher { Account = "account", Type = "Facturas", AccessKey = Key, Issued = Date, XmlPath = "object", XmlSha256 = hash };
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(voucher) })) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: Queue());
        remote.Process(new("account", "Facturas", Key, Date, Xml, null));
        Assert.Null(Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Fact] public void LostResponseThenConfirmedServerCopyClearsQueue()
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Xml));
        var handler = new Handler(request => {
            if (request.Method == HttpMethod.Post) throw new HttpRequestException("response lost");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(true) };
            response.Headers.Add("X-SRI-Content-SHA256", hash); return response;
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: Queue());
        Assert.Throws<HttpRequestException>(() => remote.Process(new("account", "Facturas", Key, Date, Xml, null)));
        Assert.NotNull(Queue().Load("account", "Facturas", Key, Date, "xml"));
        Assert.True(remote.IsAvailable(new Voucher { Id = "doc", Account = "account", Type = "Facturas", AccessKey = Key, Issued = Date }, "xml"));
        Assert.Null(Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Theory]
    [InlineData("job", 1, true, true)]
    [InlineData("other", 1, true, false)]
    [InlineData("job", 0, true, false)]
    [InlineData("job", 1, false, false)]
    public void ReconciliationRequiresOriginalJobCurrentGenerationAndAttempt(string jobId, long generation, bool attempted, bool sends)
    {
        Save(Queue());
        var job = new Execution { Id = jobId, Status = "Running", Query = new("account", Date, Date, ["Facturas"]), AttemptedKeys = attempted ? [Key] : [] };
        var state = new State { Executions = [job] };
        var count = 0;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Xml));
        var handler = new Handler(request => {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(state) };
            count++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Voucher { Account = "account", Type = "Facturas", AccessKey = Key, Issued = Date, XmlPath = "object", XmlSha256 = hash }) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new(jobId, Guid.NewGuid(), generation, "central-v1"), transferQueue: Queue(), supportsConditionalPending: true);
        remote.ReconcilePending();
        Assert.Equal(sends ? 1 : 0, count);
        Assert.Equal(!sends, Queue().Load("account", "Facturas", Key, Date, "xml") is not null);
    }
    [Fact] public void ReconciliationRetainsPendingOnExpiredLease()
    {
        Save(Queue());
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Conflict))) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 2, "central-v1"), transferQueue: Queue());
        remote.ReconcilePending();
        Assert.NotNull(Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Fact] public void PeriodicPassDoesNotUploadMissingFiles()
    {
        Save(Queue());
        var job = new Execution { Id = "job", Status = "Running", Query = new("account", Date, Date, ["Facturas"]), AttemptedKeys = [Key] };
        var calls = 0;
        using var client = new HttpClient(new Handler(request => {
            if (request.Method == HttpMethod.Post) calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new State { Executions = [job] }) };
        })) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: Queue());
        remote.ReconcilePending(confirmOnly: true);
        Assert.Equal(0, calls);
        Assert.NotNull(Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Fact] public void OldServerWithoutConditionalCapabilityNeverReceivesPendingReplay()
    {
        Save(Queue());
        var job = new Execution { Id = "job", Status = "Running", Query = new("account", Date, Date, ["Facturas"]), AttemptedKeys = [Key] };
        var uploads = 0;
        using var client = new HttpClient(new Handler(request => {
            if (request.Method == HttpMethod.Post) uploads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new State { Executions = [job] }) };
        })) { BaseAddress = new Uri("https://test.invalid") };
        new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: Queue()).ReconcilePending();
        Assert.Equal(0, uploads); Assert.NotNull(Queue().Load("account", "Facturas", Key, Date, "xml"));
    }
    [Fact] public async Task ConcurrentPassIsSkipped()
    {
        Save(Queue());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => {
            Interlocked.Increment(ref calls); entered.Set(); release.Wait(TimeSpan.FromSeconds(5));
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        })) { BaseAddress = new Uri("https://test.invalid") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: Queue());
        var first = Task.Run(() => remote.ReconcilePending(true));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        remote.ReconcilePending(true);
        release.Set(); await first;
        Assert.Equal(1, calls);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
