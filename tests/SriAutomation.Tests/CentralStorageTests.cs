using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public class CentralStorageTests : IDisposable
{
    private readonly string queueRoot = Path.Combine(Path.GetTempPath(), "sri-queue-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(queueRoot)) Directory.Delete(queueRoot, true); }

    [Theory]
    [InlineData("xml", true)]
    [InlineData("pdf", false)]
    public void CentralAvailabilityUsesOnlyServer(string extension, bool available)
    {
        var handler = new Handler((request, _) => {
            Assert.Contains("/central-available/doc/" + extension, request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(available) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), deliveryRoot: "invalid-local-path", transferQueue: new(queueRoot));
        Assert.Equal(available, remote.IsAvailable(new Voucher { Id = "doc" }, extension));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("xml")]
    public void ServerIntegrityDetectsMissingAndCorruptFiles(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "central-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var bytes = new byte[] { 1, 2, 3 };
            var hash = DownloadPipeline.Hash(bytes);
            var doc = new Voucher { XmlPath = "file.xml", XmlSha256 = hash, PdfPath = "file.pdf", PdfSha256 = hash };
            var pipeline = new DownloadPipeline(null!, new DocumentStorage(root), null!);
            Assert.False(pipeline.IsAvailable(doc, extension));
            File.WriteAllBytes(Path.Combine(root, "file." + extension), bytes);
            Assert.True(pipeline.IsAvailable(doc, extension));
            Assert.False(pipeline.IsAvailable(doc, extension == "pdf" ? "xml" : "pdf"));
            File.WriteAllBytes(Path.Combine(root, "file." + extension), [4]);
            Assert.False(pipeline.IsAvailable(doc, extension));
        } finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransferRequiresMatchingConfirmation(bool matching)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var file = new DownloadedVoucher("account", "Facturas", new string('1', 49), new DateOnly(2026, 1, 1), bytes, null);
        var voucher = new Voucher { Account = file.Account, Type = file.Type, AccessKey = file.Key, Issued = file.Issued, XmlPath = "object", XmlSha256 = matching ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : new string('0', 64) };
        using var client = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(voucher) })) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: new(queueRoot));
        if (matching) Assert.Equal(voucher.XmlSha256, remote.Process(file).XmlSha256);
        else Assert.Throws<IOException>(() => remote.Process(file));
    }

    [Fact]
    public void InterruptedTransferIsNotRetriedOrConfirmed()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("interrupted"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: new(queueRoot));
        Assert.Throws<HttpRequestException>(() => remote.Process(new("a", "Facturas", new string('1', 49), new DateOnly(2026, 1, 1), [1], null)));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(250)]
    public void CentralAvailabilityVolumeDoesNotPollDelivery(int count)
    {
        var handler = new Handler((request, _) => {
            Assert.Contains("/central-available/", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(true) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: new(queueRoot));
        for (var i = 0; i < count; i++) {
            var doc = new Voucher { Id = "doc" + i };
            Assert.True(remote.IsAvailable(doc, "pdf"));
            Assert.True(remote.IsAvailable(doc, "xml"));
        }
        Assert.Equal(count * 2, handler.Count);
    }

    [Fact]
    public void LostUploadResponseCanBeRecoveredByCheckingServerOnResume()
    {
        var stored = false;
        var handler = new Handler((request, _) => {
            if (request.Method == HttpMethod.Post) { stored = true; throw new HttpRequestException("response lost"); }
            Assert.Contains("/central-available/", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(stored) };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1, "central-v1"), transferQueue: new(queueRoot));
        var file = new DownloadedVoucher("a", "Facturas", new string('1', 49), new DateOnly(2026, 1, 1), [1], null);
        Assert.Throws<HttpRequestException>(() => remote.Process(file));
        Assert.True(remote.IsAvailable(new Voucher { Id = "doc" }, "xml"));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public void CentralModeSurvivesStateJsonRoundTripAndLegacyDefaultsToFalse()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = new State { Executions = [new Execution { CentralStorageAgent = true }] };
        var roundTrip = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(state, options), options)!;
        Assert.True(Assert.Single(roundTrip.Executions).CentralStorageAgent);
        Assert.False(JsonSerializer.Deserialize<Execution>("{}", options)!.CentralStorageAgent);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(respond(request, cancellationToken)); }
    }
}
