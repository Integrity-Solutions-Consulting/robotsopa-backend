using System.Net;
using System.Net.Http.Json;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public sealed class RemoteFileTraceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileConfirmationIsTracedOnlyAfterSuccessfulApiResponse(bool accepted)
    {
        var events = new List<string>();
        using var client = new HttpClient(new Handler(accepted)) { BaseAddress = new Uri("https://fixture.invalid") };
        var agent = new RemoteOwnedExecution(client, new RemoteWorker.Claim("job", Guid.NewGuid(), 1), events.Add);
        var input = new DownloadedVoucher("synthetic", "Facturas", new string('1', 49), new DateOnly(2026, 9, 12), null, [1, 2, 3]);
        if (accepted) Assert.NotNull(agent.Process(input));
        else Assert.Throws<HttpRequestException>(() => agent.Process(input));
        Assert.Equal("ARCHIVO_ENVIADO_API", events[0]);
        Assert.Equal(accepted ? 2 : 1, events.Count);
        if (accepted) Assert.Equal("ARCHIVO_CONFIRMADO_API", events[1]);
        Assert.DoesNotContain(events, e => e.Contains(input.Key, StringComparison.Ordinal));
    }
    private sealed class Handler(bool accepted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(accepted ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Voucher()) } : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
