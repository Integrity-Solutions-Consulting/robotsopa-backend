using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SriAutomation.Api;
using SriAutomation.Application;
using SriAutomation.Infrastructure;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public sealed class RemoteExecutionTransportTests
{
    [Fact]
    public async Task FileUploadAcceptsTwoBase64FilesAtIndividualLimitAndRejectsExcessBody()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20_000_000);
        await using var app = builder.Build();
        app.Use((c, next) => RemoteExecutionTransport.Invoke(c, next, app.Logger));
        app.MapPost("/execution-agent/v1/{id}/files", (DownloadedVoucher file) => Results.Ok(new { xml = file.Xml?.Length, pdf = file.Pdf?.Length }));
        app.MapGet("/other", (HttpContext c) => Results.Ok(c.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromMinutes(2) };
        var bytes = new byte[20_000_000];
        var voucher = new DownloadedVoucher("fixture", "Facturas", "key", new DateOnly(2026, 9, 12), bytes, bytes);
        using var accepted = await client.PostAsJsonAsync("/execution-agent/v1/job/files", voucher);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var result = await accepted.Content.ReadFromJsonAsync<Lengths>();
        Assert.Equal(20_000_000, result!.Xml); Assert.Equal(20_000_000, result.Pdf);
        using var oversized = new ByteArrayContent(new byte[RemoteExecutionTransport.FileRequestLimit + 1]);
        oversized.Headers.ContentType = new("application/json");
        using var refused = await client.PostAsync("/execution-agent/v1/job/files", oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal(20_000_000, await client.GetFromJsonAsync<long>("/other"));
    }

    [Fact]
    public void IndividualLimitsAreEnforcedBeforeParsingOrStorage()
    {
        var pipeline = new DownloadPipeline(null!, null!, null!);
        var oversized = new byte[20_000_001];
        var input = new DownloadedVoucher("fixture", "Facturas", "key", new DateOnly(2026, 9, 12), oversized, null);
        Assert.Throws<DocumentSizeException>(() => pipeline.Process(input));
        Assert.Throws<DocumentSizeException>(() => pipeline.Process(input with { Xml = null, Pdf = oversized }));
        Assert.Throws<DocumentSizeException>(() => pipeline.Process(input with { Xml = [], Pdf = null }));
    }

    [Theory]
    [InlineData("lease", 409, "LEASE_EXPIRED_OR_CHANGED")]
    [InlineData("validation", 400, "DOCUMENT_OR_REQUEST_INVALID")]
    [InlineData("storage", 503, "STORAGE_UNAVAILABLE")]
    public async Task ErrorsAreClassifiedWithoutLeakingExceptionDetails(string failure, int status, string reason)
    {
        var context = new DefaultHttpContext(); context.Request.Path = "/execution-agent/v1/job/files"; context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        await RemoteExecutionTransport.Invoke(context, _ => throw failure switch
        {
            "lease" => new ExecutionLeaseException(),
            "validation" => new ArgumentException("private-document-data"),
            _ => new IOException("private-document-data")
        }, loggerFactory.CreateLogger("test"));
        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0; var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains(reason, body); Assert.DoesNotContain("private-document-data", body);
    }
    private sealed record Lengths(int? Xml, int? Pdf);

    [Fact]
    public async Task SyntheticPdfAndXmlAreConfirmedStoredAndNotDuplicated()
    {
        var root = Path.Combine(Path.GetTempPath(), "sopa-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = new JsonStateRepository(Path.Combine(root, "state"));
            repo.Update(s => { s.Accounts.Add(new("fixture", "Fixture", "1790000000001")); return true; });
            var storage = new DocumentStorage(Path.Combine(root, "documents"));
            var pipeline = new DownloadPipeline(repo, storage, new XmlDocumentParser());
            var blocked = Path.Combine(root, "blocked"); await File.WriteAllTextAsync(blocked, "fixture");
            var failingPipeline = new DownloadPipeline(repo, new DocumentStorage(Path.Combine(blocked, "documents")), new XmlDocumentParser());
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.Use((c, next) => RemoteExecutionTransport.Invoke(c, next, app.Logger));
            app.MapPost("/execution-agent/v1/{id}/files", (DownloadedVoucher input) => Results.Ok(pipeline.Process(input)));
            app.MapPost("/execution-agent/v1/{id}/storage-test", (DownloadedVoucher input) => Results.Ok(failingPipeline.Process(input)));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var key = PhaseTwoTests.Key;
            var input = new DownloadedVoucher("fixture", "Facturas", key, new DateOnly(2026, 9, 12), PhaseTwoTests.Xml(key), PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key]));
            using var response = await client.PostAsJsonAsync("/execution-agent/v1/job/files", input);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var document = (await response.Content.ReadFromJsonAsync<Voucher>())!;
            Assert.Equal(input.Xml, await File.ReadAllBytesAsync(storage.Resolve(document.XmlPath!)));
            Assert.Equal(input.Pdf, await File.ReadAllBytesAsync(storage.Resolve(document.PdfPath!)));
            using var repeat = await client.PostAsJsonAsync("/execution-agent/v1/job/files", input);
            Assert.Equal(document.Id, (await repeat.Content.ReadFromJsonAsync<Voucher>())!.Id);
            Assert.Single(repo.Read(s => s.Documents));
            using var invalid = await client.PostAsJsonAsync("/execution-agent/v1/job/files", input with { Xml = [1, 2, 3] });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Single(repo.Read(s => s.Documents));
            using var storageError = await client.PostAsJsonAsync("/execution-agent/v1/job/storage-test", input);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, storageError.StatusCode);
            Assert.Contains("STORAGE_UNAVAILABLE", await storageError.Content.ReadAsStringAsync());
            Assert.Single(repo.Read(s => s.Documents));
            var key2 = key[..^1] + "2";
            var smallPdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key2]);
            var largePdf = new byte[19_999_000]; Array.Fill(largePdf, (byte)' '); smallPdf.CopyTo(largePdf, 0);
            using var nearLimit = await client.PostAsJsonAsync("/execution-agent/v1/job/files", input with { Key = key2, Xml = PhaseTwoTests.Xml(key2), Pdf = largePdf });
            Assert.Equal(HttpStatusCode.OK, nearLimit.StatusCode);
            var second = (await nearLimit.Content.ReadFromJsonAsync<Voucher>())!;
            Assert.Equal(largePdf.Length, new FileInfo(storage.Resolve(second.PdfPath!)).Length);
            Assert.Equal(2, repo.Read(s => s.Documents.Count));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
