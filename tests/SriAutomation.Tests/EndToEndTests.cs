using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Playwright;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public sealed class EndToEndTests
{
    [LocalStackFact]
    public async Task ApiWorkerStorageAndBrowser()
    {
        var root = FindRoot();
        var password = File.ReadLines(Path.Combine(root, ".env")).Single(l => l.StartsWith("SRI_ADMIN_PASSWORD=")).Split('=', 2)[1];
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri("http://localhost:3000") };
        client.DefaultRequestHeaders.Add("X-SRI-Test-Data", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/accounts")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = File.ReadLines(Path.Combine(root, ".env")).Single(l => l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=', 2)[1], password }); Assert.True(login.IsSuccessStatusCode, "Login del panel falló.");
        var id = "qa-" + Guid.NewGuid().ToString("N")[..10];
        Assert.True((await client.PostAsJsonAsync("/api/v1/accounts", new Account(id, "QA FICTICIA — no es cuenta SRI real"))).IsSuccessStatusCode);
        var xml = """
          <autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>0000123456789</numeroAutorizacion><fechaAutorizacion>2026-10-01T09:00:00-05:00</fechaAutorizacion><comprobante><![CDATA[<factura><infoTributaria><razonSocial>EMISOR FICTICIO QA</razonSocial><ruc>1790000000001</ruc><claveAcceso>0109202601179000000000110010010000000011234567811</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria><infoFactura><fechaEmision>12/09/2026</fechaEmision><razonSocialComprador>RECEPTOR FICTICIO QA</razonSocialComprador><identificacionComprador>0012345678</identificacionComprador><totalSinImpuestos>10.00</totalSinImpuestos><totalConImpuestos><totalImpuesto><valor>1.50</valor></totalImpuesto></totalConImpuestos><importeTotal>11.50</importeTotal></infoFactura></factura>]]></comprobante></autorizacion>
          """;
        var imported = await client.PostAsync("/api/v1/documents/import/" + id, new StringContent(xml, Encoding.UTF8, "application/xml"));
        Assert.True(imported.IsSuccessStatusCode, "La importación XML falló.");
        var document = await imported.Content.ReadFromJsonAsync<Voucher>(); Assert.NotNull(document);
        var pdf = PdfFixture.Build(document.AccessKey);
        var attached = await client.PostAsync($"/api/v1/documents/{document.Id}/pdf", new ByteArrayContent(pdf));
        Assert.True(attached.IsSuccessStatusCode, "La asociación PDF falló.");
        var query = new Query(id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), ["Facturas"], "Stored");
        var accepted = await client.PostAsJsonAsync("/api/v1/executions", query); Assert.True(accepted.IsSuccessStatusCode);
        var job = await accepted.Content.ReadFromJsonAsync<Execution>(); Assert.NotNull(job);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            job = await client.GetFromJsonAsync<Execution>("/api/v1/executions/" + job.Id);
            Assert.NotNull(job);
            if (job.Status is not "Pending" and not "Running") break;
            await Task.Delay(1000);
        }
        Assert.Equal("Completed", job.Status); Assert.Equal(1, job.Processed);
        var report = await client.GetAsync($"/api/v1/reports/{id}/2026/9"); Assert.True(report.IsSuccessStatusCode);
        using (var book = new XLWorkbook(new MemoryStream(await report.Content.ReadAsByteArrayAsync()))) { Assert.Equal(3, book.Worksheets.Count); Assert.Equal("0012345678", book.Worksheet("Documentos").Cell(2, 5).GetString()); }
        Assert.True((await client.GetAsync($"/api/v1/documents/{document.Id}/xml")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync($"/api/v1/documents/{document.Id}/pdf")).IsSuccessStatusCode);
        var sriJobResponse = await client.PostAsJsonAsync("/api/v1/executions", query with { Mode = "Sri" }); Assert.True(sriJobResponse.IsSuccessStatusCode);
        var sriJob = await sriJobResponse.Content.ReadFromJsonAsync<Execution>();
        for (var attempt = 0; attempt < 15; attempt++)
        { sriJob = await client.GetFromJsonAsync<Execution>("/api/v1/executions/" + sriJob!.Id); if (sriJob!.Status == "RequiresHumanAction") break; await Task.Delay(1000); }
        Assert.Equal("RequiresHumanAction", sriJob!.Status);
        var scheduleResponse = await client.PostAsJsonAsync("/api/v1/schedules", new Schedule { Query = query, Frequency = "Once", Next = DateTimeOffset.UtcNow.AddSeconds(5) }); Assert.True(scheduleResponse.IsSuccessStatusCode);
        var schedule = await scheduleResponse.Content.ReadFromJsonAsync<Schedule>(); Assert.NotNull(schedule);
        for (var attempt = 0; attempt < 20; attempt++) { var schedules = await client.GetFromJsonAsync<List<Schedule>>("/api/v1/schedules"); if (schedules!.Single(s => s.Id == schedule.Id).Enabled == false) break; await Task.Delay(1000); }
        Assert.False((await client.GetFromJsonAsync<List<Schedule>>("/api/v1/schedules"))!.Single(s => s.Id == schedule.Id).Enabled);
        Assert.True((await client.DeleteAsync("/api/v1/schedules/" + schedule.Id)).IsSuccessStatusCode);
        client.DefaultRequestHeaders.Remove("X-SRI-Test-Data");
        var normalDashboard = await client.GetStringAsync("/api/v1/dashboard");
        Assert.True(!normalDashboard.Contains(id, StringComparison.Ordinal), "El dashboard operativo no debe presentar ejecuciones de QA.");
        var normalDocuments = await client.GetFromJsonAsync<JsonElement>("/api/v1/documents?account=" + id);
        Assert.Equal(0, normalDocuments.GetProperty("total").GetInt32());
        client.DefaultRequestHeaders.Add("X-SRI-Test-Data", "1");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync("http://localhost:3000");
        await page.GetByLabel("Usuario",new(){Exact=true}).FillAsync(File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=',2)[1]);await page.GetByLabel("Contraseña del panel").FillAsync(password);
        await page.GetByRole(AriaRole.Button,new(){Name="Entrar al panel"}).ClickAsync();
        await page.GetByRole(AriaRole.Heading,new(){Name="Inicio",Exact=true}).WaitForAsync();
        await Assertions.Expect(page.Locator("aside nav a")).ToHaveCountAsync(4);
        var screenshots=Path.Combine(root,"test-artifacts");Directory.CreateDirectory(screenshots);
        await page.ScreenshotAsync(new(){Path=Path.Combine(screenshots,"inicio-operativo.png"),FullPage=true});
        File.WriteAllText(Path.Combine(screenshots,"integration-result.json"), JsonSerializer.Serialize(new { account = id, execution = job.Id, status = job.Status, sri = sriJob.Status, output = document.XmlPath }));
    }
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ".env"))) return dir.FullName;
        throw new InvalidOperationException("No se encontró .env local.");
    }
}
public sealed class LocalStackFactAttribute : FactAttribute
{ public LocalStackFactAttribute() { if (Environment.GetEnvironmentVariable("SRI_TEST_STACK") != "1") Skip = "Requiere SRI_TEST_STACK=1, Chromium y los tres servicios locales."; } }
public static class PdfFixture
{
    public static byte[] Build(string key)
    {
        return BuildLines(["FICTICIO QA " + key]);
    }
    public static byte[] BuildLines(string[] lines)
    {
        var content = "BT /F1 10 Tf 30 760 Td " + string.Join(" 0 -14 Td ", lines.Select(line => "(" + line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ") Tj")) + " ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"];
        var buffer = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(buffer.Length); buffer.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = buffer.Length; buffer.Append($"xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) buffer.Append($"{offset:D10} 00000 n \n");
        buffer.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(buffer.ToString());
    }
}
