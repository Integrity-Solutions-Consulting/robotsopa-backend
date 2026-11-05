using System.Text;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;

// Test-only local Playwright fixture. It never sends traffic to the SRI.
sealed class ControlledPortal(IStateRepository repository, IDownloadPipeline downloads) : ISriComprobanteService
{
    private const string Key = "0109202601179000000000110010010000000011234567811";
    public Task Execute(Query query, CancellationToken cancellation) => throw new InvalidOperationException("Se requiere ejecución persistente.");
    public async Task Execute(Execution execution, CancellationToken cancellation)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        using var registration = cancellation.Register(() => { _ = context.CloseAsync(); });
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync(); } catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                var path = request.Request.Url!.AbsolutePath;
                var barrier = Environment.GetEnvironmentVariable("SRI_FIXTURE_BARRIER");
                if (path == "/document.xml" && !string.IsNullOrWhiteSpace(barrier))
                {
                    var marker = Path.Combine(barrier, "download-started");
                    var first = false;
                    try { using var markerFile = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read); using var writer = new StreamWriter(markerFile); writer.Write(Environment.ProcessId); first = true; }
                    catch (IOException) when (File.Exists(marker)) { }
                    if (first) while (!File.Exists(Path.Combine(barrier, "release-download"))) await Task.Delay(100);
                }
                var bytes = path == "/document.xml" ? Xml() : path == "/document.pdf" ? Pdf() : Encoding.UTF8.GetBytes(path == "/query" ? "{\"ok\":true}" : Html);
                request.Response.ContentType = path == "/query" ? "application/json" : path.EndsWith(".xml") ? "application/xml" : path.EndsWith(".pdf") ? "application/pdf" : "text/html";
                if (path.StartsWith("/document.")) request.Response.Headers.Add("Content-Disposition", "attachment; filename=" + path[1..]);
                request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
            }
        });
        var page = await context.NewPageAsync();
        var url = $"http://127.0.0.1:{port}/";
        await page.GotoAsync(url);
        var profile = new PortalProfile { Url = url, Verified = true, VerifiedAt = "2026-11-05", Evidence = "Fixture aislado", AccountIdentity = "#account", Authenticated = "#authenticated", Login = "#login", HumanChallenge = "#challenge", Date = "#date", Type = "#type", TypeValues = new() { ["Facturas"] = "01" }, Search = "#search", Ready = "#ready", ResponseUrlPattern = "/query$", Rows = "tbody tr", Empty = "#empty", Next = "#next", NextDisabled = "#next:disabled", Key = ".key", Issued = ".issued", Xml = ".xml", Pdf = ".pdf", MinQueryIntervalMs = 0 };
        profile.Validate(true);
        await new SriExecutionEngine(repository, new TracedPipeline(downloads)).Run(page, profile, execution, "1790000000001", cancellation);
        listener.Stop(); await server;
        Console.WriteLine("FIXTURE_ENGINE_COMPLETED");
    }
    private const string Html = """
        <html><body><div id="authenticated">Fixture</div><span id="account">1790000000001</span><div id="login" hidden></div><div id="challenge" hidden></div>
        <input id="date"><select id="type"><option value="01">Factura</option></select><button id="search" onclick="load()">Consultar</button>
        <div id="ready"><table><tbody></tbody></table><div id="empty" hidden>Sin documentos</div></div><button id="next" disabled>Siguiente</button>
        <script>async function load(){await fetch('/query',{method:'POST'});document.querySelector('tbody').innerHTML='<tr><td class="key">0109202601179000000000110010010000000011234567811</td><td class="issued">12/09/2026</td><td><a class="xml" href="/document.xml" download>XML</a><a class="pdf" href="/document.pdf" download>PDF</a></td></tr>';}</script></body></html>
        """;
    private static byte[] Xml() => Encoding.UTF8.GetBytes($"""
        <autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>{Key}</numeroAutorizacion><fechaAutorizacion>2026-09-12T12:00:00-05:00</fechaAutorizacion><comprobante><![CDATA[<factura><infoTributaria><ruc>1790000000001</ruc><razonSocial>EMISOR FICTICIO</razonSocial><claveAcceso>{Key}</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria><infoFactura><fechaEmision>12/09/2026</fechaEmision><razonSocialComprador>RECEPTOR FICTICIO</razonSocialComprador><identificacionComprador>0012345678</identificacionComprador><totalSinImpuestos>10.00</totalSinImpuestos><totalConImpuestos><totalImpuesto><valor>1.50</valor></totalImpuesto></totalConImpuestos><importeTotal>11.50</importeTotal></infoFactura></factura>]]></comprobante></autorizacion>
        """);
    private static byte[] Pdf()
    {
        var content = $"BT /F1 10 Tf 30 760 Td (CLAVE DE ACCESO: {Key}) Tj ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"];
        var buffer = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(buffer.Length); buffer.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = buffer.Length; buffer.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) buffer.Append($"{offset:D10} 00000 n \n");
        buffer.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(buffer.ToString());
    }
}

sealed class TracedPipeline(IDownloadPipeline inner) : IDownloadPipeline
{
    public bool IsAvailable(Voucher document, string extension) => inner.IsAvailable(document, extension);
    public Voucher Process(DownloadedVoucher download)
    {
        try { return inner.Process(download); }
        catch (Exception ex) { Console.Error.WriteLine("FIXTURE_PIPELINE: " + ex); throw; }
    }
}
