using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public sealed class OperatorIncidentHttpTests
{
    private static RemoteOwnedExecution Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://synthetic.local") },
            new RemoteWorker.Claim("job", Guid.NewGuid(), 1));

    [Fact]
    public void Retries503AndAccepts200()
    {
        var calls = 0;
        var client = Client(new StubHandler((request, _) =>
            ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : VoucherResponse()));
        var result = client.Process(Payload());
        Assert.Equal("key", result.AccessKey); Assert.Equal(2, calls);
    }

    [Fact]
    public void LostResponseIsRetriedWithoutDuplicateDocument()
    {
        var calls = 0; var accepted = new HashSet<string>();
        var client = Client(new StubHandler(async (request, _) =>
        {
            await request.Content!.ReadAsStringAsync();
            var hash = "synthetic-pdf-hash";
            var first = calls++ == 0;
            accepted.Add(hash);
            if (first) throw new HttpRequestException("respuesta perdida");
            return VoucherResponse();
        }));
        _ = client.Process(Payload());
        Assert.Equal(2, calls); Assert.Single(accepted);
    }

    [Fact]
    public void ExpiredLeaseReturns409WithoutRetryOrWrite()
    {
        var calls = 0; var writes = 0;
        var client = Client(new StubHandler((_, _) => { calls++; writes++; return new HttpResponseMessage(HttpStatusCode.Conflict); }));
        Assert.Throws<HttpRequestException>(() => client.Process(Payload()));
        Assert.Equal(1, calls); Assert.Equal(1, writes);
    }

    [Fact]
    public void IOExceptionTemporaryIsRetainedThenCleaned()
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-temp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var storage = new DocumentStorage(root); var document = new Voucher { Account = "a", Type = "Facturas", AccessKey = PhaseTwoTests.Key, Issued = new(2026, 9, 12) };
            var target = Path.Combine(root, "2026", "09-Septiembre", "a", "Facturas", PhaseTwoTests.Key + ".pdf"); Directory.CreateDirectory(target);
            Assert.Throws<IOException>(() => storage.Save(document, "pdf", PdfFixture.BuildLines(["CLAVE DE ACCESO: " + PhaseTwoTests.Key])));
            var temporary = Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp").Single(); System.IO.File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddHours(-25));
            storage.CheckAccess(); Assert.False(File.Exists(temporary)); Assert.True(Directory.Exists(target));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptySubmissionIsRejectedBeforeFilesEndpointAndPartialPdfCanContinue()
    {
        Assert.False(SriExecutionEngine.HasNewFiles(null, null)); Assert.True(SriExecutionEngine.HasNewFiles(null, [1]));
        var state = new State(); state.Accounts.Add(new("a", "synthetic", "1790000000001"));
        var repo = new MemoryRepository(state); var root = Path.Combine(Path.GetTempPath(), "sri-partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + PhaseTwoTests.Key, "FECHA DE EMISION: 12/09/2026", "IMPORTE TOTAL: 11.50"]);
            var document = new DownloadPipeline(repo, new DocumentStorage(root), new XmlDocumentParser()).Process(new("a", "Facturas", PhaseTwoTests.Key, new(2026, 9, 12), null, pdf));
            Assert.Null(document.XmlPath); Assert.NotNull(document.PdfSha256); Assert.Equal("Revisar", document.Status); Assert.Single(state.Documents);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static DownloadedVoucher Payload() => new("a", "Facturas", "key", new(2026, 9, 12), null, [1, 2, 3]);
    private static HttpResponseMessage VoucherResponse() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new Voucher { AccessKey = "key" }) };
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> action) : this((r, t) => Task.FromResult(action(r, t))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class MemoryRepository(State state) : IStateRepository
    {
        public T Read<T>(Func<State, T> read) => read(state);
        public T Update<T>(Func<State, T> mutate) => mutate(state);
    }
}
