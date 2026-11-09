using System.Net;
using System.Net.Http.Json;
using SriAutomation.Domain;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public class ConnectionRetryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadRecoversFromNetworkTimeoutOrHttpFailure(int failure)
    {
        var handler = new ScriptedHandler(n => n == 1 ? failure switch {
            0 => throw new HttpRequestException("offline"),
            1 => throw new TaskCanceledException("timeout"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        } : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new State()) });
        var trace = new List<string>();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1), trace.Add);
        Assert.True(remote.Read(_ => true));
        Assert.Equal(2, handler.Count);
        Assert.Contains("ConnectionRecovered", trace);
    }

    [Fact]
    public void DefinitiveHttpErrorIsNotRetried()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1));
        Assert.Throws<HttpRequestException>(() => remote.Read(_ => true));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public void CancellationStopsBeforeRetry()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new ScriptedHandler(_ => { cancel.Cancel(); throw new HttpRequestException("offline"); });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1), cancellation: cancel.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => remote.Read(_ => true));
        Assert.Equal(1, handler.Count);
    }

    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(++Count));
    }
}
