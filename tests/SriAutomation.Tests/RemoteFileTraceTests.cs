using System.Net;
using System.Net.Http.Json;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.LocalAgent;
namespace SriAutomation.Tests;
public sealed class RemoteFileTraceTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(500)]
    [InlineData(503)]
    public void FileResponseRecordsSafeHttpStatusWithoutPrivateContent(int status)
    {
        var events = new List<string>();
        using var client = new HttpClient(new Handler(status)) { BaseAddress = new Uri("https://fixture.invalid") };
        var agent = new RemoteOwnedExecution(client, new RemoteWorker.Claim("job", Guid.NewGuid(), 1), events.Add);
        var input = new DownloadedVoucher("synthetic", "Facturas", new string('1', 49), new DateOnly(2026, 9, 12), null, [1, 2, 3]);
        if (status == 200) Assert.NotNull(agent.Process(input));
        else Assert.Throws<HttpRequestException>(() => agent.Process(input));
        Assert.Equal("ARCHIVO_ENVIADO_API", events[0]);
        Assert.Contains($"FileApiStatus status={status}", events);
        Assert.Equal(status == 200, events.Contains("ARCHIVO_CONFIRMADO_API"));
        Assert.DoesNotContain(events, e => e.Contains(input.Key, StringComparison.Ordinal) || e.Contains("private", StringComparison.Ordinal));
    }
    [Fact]
    public void TransportFailureRecordsOnlyExceptionType()
    {
        var events = new List<string>();
        using var client = new HttpClient(new Handler(0)) { BaseAddress = new Uri("https://fixture.invalid") };
        var agent = new RemoteOwnedExecution(client, new RemoteWorker.Claim("job", Guid.NewGuid(), 1), events.Add);
        Assert.Throws<HttpRequestException>(() => agent.Process(new("synthetic", "Facturas", new string('1', 49), new(2026, 9, 12), null, [1])));
        Assert.Equal(new[] { "ARCHIVO_ENVIADO_API", "FileTransportError type=HttpRequestException" }, events);
    }
    [Fact]
    public async Task LeaseFailureReportsHttpStatus()
    {
        var events = new List<string>();
        using var client = new HttpClient(new Handler(409)) { BaseAddress = new Uri("https://fixture.invalid") };
        var agent = new RemoteOwnedExecution(client, new RemoteWorker.Claim("job", Guid.NewGuid(), 1), events.Add);
        Assert.False(await agent.Renew(CancellationToken.None));
        Assert.Equal(new[] { "LeaseRenewalHttpStatus status=409" }, events);
    }
    private sealed class Handler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (status == 0) throw new HttpRequestException("private");
            return Task.FromResult(status == 200 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Voucher()) } : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private") });
        }
    }
}
