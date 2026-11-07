using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public sealed class ReportDeliveryTests
{
    [Fact]
    public async Task DeliveryVersionsExistingWorkbook()
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            static byte[] Workbook(string value)
            {
                using var book = new XLWorkbook(); book.AddWorksheet("Datos").Cell(1, 1).Value = value;
                using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
            }
            var old = Workbook("anterior"); var next = Workbook("nuevo");
            var path = Path.Combine(root, "cuenta", "reporte.xlsx"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, old);
            var handler = new DeliveryHandler(next);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid") };
            await DeliveryAgent.PollOnce(client, root, Path.Combine(root, "delivery.lock"), CancellationToken.None);
            Assert.Equal(next, await File.ReadAllBytesAsync(path));
            var backup = Assert.Single(Directory.GetFiles(Path.Combine(root, "cuenta", ".versiones"), "*.xlsx"));
            Assert.Equal(old, await File.ReadAllBytesAsync(backup));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeliveryRejectsInvalidExcelWithoutChangingExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-delivery-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "cuenta", "reporte.xlsx"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var workbook = new XLWorkbook()) { workbook.AddWorksheet("Datos"); workbook.SaveAs(path); }
            var before = await File.ReadAllBytesAsync(path);
            using var client = new HttpClient(new DeliveryHandler("archivo inválido"u8.ToArray())) { BaseAddress = new Uri("https://fixture.invalid") };
            await DeliveryAgent.PollOnce(client, root, Path.Combine(root, "delivery.lock"), CancellationToken.None);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.False(Directory.Exists(Path.Combine(root, "cuenta", ".versiones")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class DeliveryHandler(byte[] bytes) : HttpMessageHandler
    {
        private readonly Guid id = Guid.NewGuid();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pending"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new PendingRemoteDelivery(id, "a", "d", "xlsx", "cuenta/reporte.xlsx", DownloadPipeline.Hash(bytes), 0) }) });
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
