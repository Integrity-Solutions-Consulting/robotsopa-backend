using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SriAutomation.Api;

public sealed class RemoteAgentStatusTests
{
    [Fact]
    public async Task HttpStatusRequiresIdentityAndDoesNotOfferExecution()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        RemoteAgentStatus.Map(app, c => c.Request.Headers["X-Test-Identity"] == "valid" ? "fixture" : null);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var denied = await client.GetAsync("/execution-agent/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Identity", "valid");
        using var accepted = await client.GetAsync("/execution-agent/v1/status");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var json = await accepted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(json.GetProperty("authenticated").GetBoolean());
        Assert.False(json.GetProperty("executionAvailable").GetBoolean());
        await app.StopAsync();
    }
}
