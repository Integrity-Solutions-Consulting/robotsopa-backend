using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace SriAutomation.Tests;

public sealed class FixtureServer : IAsyncDisposable
{
    private readonly WebApplication app;
    public int Queries;
    public int Files;
    public string Url => app.Urls.Single();
    private FixtureServer(WebApplication app) { this.app = app; }
    public static async Task<FixtureServer> Start(string html, bool empty = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); var server = new FixtureServer(app);
        app.MapGet("/", () => Results.Content(html, "text/html"));
        app.MapPost("/query", async (HttpRequest request) =>
        {
            Interlocked.Increment(ref server.Queries);
            var data = await System.Text.Json.JsonDocument.ParseAsync(request.Body); var page = data.RootElement.GetProperty("page").GetInt32();
            return Results.Json(new { keys = empty ? Array.Empty<string>() : new[] { page == 0 ? PhaseTwoTests.Key : PhaseTwoTests.Key[..^1] + "2" }, next = !empty && page == 0 });
        });
        app.MapGet("/files/{filename}", (string filename) =>
        {
            Interlocked.Increment(ref server.Files); var key = Path.GetFileNameWithoutExtension(filename); var xml = filename.EndsWith(".xml");
            return Results.File(xml ? PhaseTwoTests.Xml(key) : PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key]), xml ? "application/xml" : "application/pdf", filename);
        });
        await app.StartAsync(); return server;
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
