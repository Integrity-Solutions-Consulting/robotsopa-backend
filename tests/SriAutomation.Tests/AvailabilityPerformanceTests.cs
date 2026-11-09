using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SriAutomation.Domain;
using SriAutomation.LocalAgent;
using Xunit.Abstractions;

namespace SriAutomation.Tests;

public sealed class AvailabilityPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(250)]
    public void ConfirmedIdentityAvoidsRepeatedStateRequests(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-perf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = Run(false); var after = Run(true);
            Assert.Equal(count * 4, before.requests);
            Assert.Equal(count * 2, after.requests);
            output.WriteLine($"documents={count} beforeRequests={before.requests} afterRequests={after.requests} beforeMs={before.ms} afterMs={after.ms}");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        (int requests, long ms) Run(bool knownIdentity)
        {
            var handler = new Handler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var remote = new RemoteOwnedExecution(client, new("job", Guid.NewGuid(), 1), deliveryRoot: root);
            if (knownIdentity) remote.SetDeliveryIdentity("a", "1790000000001");
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var doc = new Voucher { Id = "doc" + i, Account = "a", Type = "Facturas", AccessKey = i.ToString("D49"), Issued = new DateOnly(2026, 9, 12) };
                Assert.False(remote.IsAvailable(doc, "xml"));
                Assert.False(remote.IsAvailable(doc, "pdf"));
            }
            return (handler.Count, watch.ElapsedMilliseconds);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            var state = new State(); state.Accounts.Add(new("a", "Fixture", "1790000000001"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = request.RequestUri!.AbsolutePath.EndsWith("/state") ? JsonContent.Create(state) : JsonContent.Create(false)
            });
        }
    }
}
