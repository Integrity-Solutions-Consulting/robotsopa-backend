using System.Text;
using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class PhaseTwoTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sri-phase2-" + Guid.NewGuid().ToString("N"));
    public const string Key = "0109202601179000000000110010010000000011234567811";
    public PhaseTwoTests() { Directory.CreateDirectory(directory); }
    [Fact]
    public void LegacyQaIsRetainedButExcludedFromNormalDashboard()
    {
        var state = new State(); state.Accounts.Add(new("qa-old", "QA FICTICIA heredada"));
        state.Documents.Add(new() { Account = "qa-old", Source = "Sri" });
        state.Executions.Add(new() { Query = new("qa-old", new(2026, 9, 12), new(2026, 9, 12), ["Facturas"]), Status = "Completed" });
        DataScope.MarkLegacyFixtures(state);
        var json = JsonSerializer.SerializeToElement(DashboardData.Build(state, new DocumentStorage(directory)));
        Assert.Equal(0, json.GetProperty("documents").GetInt32()); Assert.Equal(0, json.GetProperty("completed").GetInt32());
        Assert.Equal(1, JsonSerializer.SerializeToElement(DashboardData.Build(state, new DocumentStorage(directory), true)).GetProperty("documents").GetInt32());
        Assert.Single(state.Documents); Assert.Single(state.Executions);
    }
    [Fact]
    public void EmptyStateShowsZeroAndNoHardcodedActivity()
    {
        var json = JsonSerializer.SerializeToElement(DashboardData.Build(new State(), new DocumentStorage(directory)));
        Assert.Equal(0, json.GetProperty("documents").GetInt32()); Assert.Equal(0, json.GetProperty("downloaded").GetInt32());
        Assert.Empty(json.GetProperty("recent").EnumerateArray()); Assert.Equal(JsonValueKind.Null, json.GetProperty("last").ValueKind);
    }
    [Fact]
    public void SessionIsEncryptedAccountBoundExpiresAndDetectsTampering()
    {
        var key = Path.Combine(directory, "key"); File.WriteAllBytes(key, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var vault = new SessionVault(directory, key);
        vault.Save(new("account-a", "1790000000001", DateTimeOffset.UtcNow.AddMinutes(5), "SECRET-COOKIE-TEST"));
        Assert.DoesNotContain("SECRET-COOKIE-TEST", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "account-a.session"))));
        Assert.Equal("SECRET-COOKIE-TEST", vault.Load("account-a", "1790000000001").StorageState);
        Assert.Throws<HumanActionRequiredException>(() => vault.Load("account-a", "9999999999001"));
        File.Copy(Path.Combine(directory, "account-a.session"), Path.Combine(directory, "account-b.session"));
        Assert.Throws<HumanActionRequiredException>(() => vault.Load("account-b", "1790000000001"));
        vault.Save(new("expired", "1790000000001", DateTimeOffset.UtcNow.AddMinutes(-1), "cookie"));
        Assert.Throws<HumanActionRequiredException>(() => vault.Load("expired", "1790000000001"));
        var bytes = File.ReadAllBytes(Path.Combine(directory, "account-a.session")); bytes[^1] ^= 1; File.WriteAllBytes(Path.Combine(directory, "account-a.session"), bytes);
        Assert.Throws<HumanActionRequiredException>(() => vault.Load("account-a", "1790000000001"));
        vault.Revoke("account-a"); Assert.False(File.Exists(Path.Combine(directory, "account-a.session")));
    }
    [Fact]
    public void UnverifiedProfileCannotStartRealQueries()
    { Assert.Throws<HumanActionRequiredException>(() => new PortalProfile().Validate()); }
    [Theory]
    [InlineData("Facturas", "IMPORTE TOTAL", "11.50")]
    [InlineData("Retenciones", "TOTAL RETENIDO", "1.50")]
    [InlineData("Notas-Credito", "VALOR MODIFICACION", "7.00")]
    public void PdfFallbackUsesReceiverLabelsAndTypeSpecificTotals(string type, string totalLabel, string total)
    {
        var pdf = PdfFixture.BuildLines(["RUC: 1790000000001", "RAZON SOCIAL: EMISOR FICTICIO", "CLAVE DE ACCESO: " + Key,
            "FECHA DE EMISION: 12/09/2026", type == "Retenciones" ? "RAZON SOCIAL SUJETO RETENIDO: RECEPTOR FICTICIO" : "RECEPTOR: RECEPTOR FICTICIO", "IDENTIFICACION: 0012345678", totalLabel + ": " + total]);
        var d = new PdfVoucherParser().Parse("account", type, Key, new(2026, 9, 12), pdf);
        Assert.Equal("RECEPTOR FICTICIO", d.Recipient); Assert.Equal("0012345678", d.Identification); Assert.Equal(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), d.Total);
        Assert.Equal("Revisar", d.Status); Assert.Null(d.Authorization);
    }
    [Fact]
    public void PdfFromAnotherDocumentIsRejected()
    {
        var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + Key]);
        Assert.Throws<ArgumentException>(() => new PdfVoucherParser().Parse("a", "Facturas", new string('9', 49), new(2026, 9, 12), pdf));
    }
    public static byte[] Xml(string key = Key) => Encoding.UTF8.GetBytes($"""
        <autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>{key}</numeroAutorizacion><fechaAutorizacion>2026-09-12T12:00:00-05:00</fechaAutorizacion><comprobante><![CDATA[<factura><infoTributaria><ruc>1790000000001</ruc><razonSocial>EMISOR FICTICIO</razonSocial><claveAcceso>{key}</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria><infoFactura><fechaEmision>12/09/2026</fechaEmision><razonSocialComprador>RECEPTOR FICTICIO</razonSocialComprador><identificacionComprador>0012345678</identificacionComprador><totalSinImpuestos>10.00</totalSinImpuestos><totalConImpuestos><totalImpuesto><valor>1.50</valor></totalImpuesto></totalConImpuestos><importeTotal>11.50</importeTotal></infoFactura></factura>]]></comprobante></autorizacion>
        """);
    [Fact]
    public void DownloadPipelineHashesAndAssociatesWithoutDuplicates()
    {
        var repository = new JsonStateRepository(Path.Combine(directory, "state")); var storage = new DocumentStorage(Path.Combine(directory, "out"));
        repository.Update(s => { s.Accounts.Add(new("a", "Cuenta", "1790000000001")); return true; });
        var pipeline = new DownloadPipeline(repository, storage, new XmlDocumentParser());
        var download = new DownloadedVoucher("a", "Facturas", Key, new(2026, 9, 12), Xml(), PdfFixture.BuildLines(["CLAVE DE ACCESO: " + Key]));
        var d = pipeline.Process(download); pipeline.Process(download);
        Assert.True(pipeline.IsAvailable(d, "xml")); Assert.True(pipeline.IsAvailable(d, "pdf"));
        Assert.Equal(DownloadPipeline.Hash(Xml()), d.XmlSha256); Assert.NotNull(d.PdfSha256); Assert.Equal("Sri", d.Source);
        Assert.Single(repository.Read(s => s.Documents));
        new DocumentIndexer(repository, storage, new XmlDocumentParser()).Rebuild("a");
        Assert.Equal("Sri", repository.Read(s => s.Documents.Single().Source));
        Assert.Throws<ArgumentException>(() => pipeline.Process(download with { Key = new string('9', 49) }));
        Assert.Throws<ArgumentException>(() => pipeline.Process(download with { Pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + new string('9', 49)]) }));
        Assert.Throws<ArgumentException>(() => pipeline.Process(download with { Pdf = "<html>Error de sesión</html>"u8.ToArray() }));
        Assert.Throws<ArgumentException>(() => pipeline.Process(download with { Xml = "<html>Error de sesión</html>"u8.ToArray() }));
        File.WriteAllText(storage.Resolve(d.XmlPath!), "corrupt");
        Assert.False(pipeline.IsAvailable(d, "xml"));
    }
    public void Dispose() { Directory.Delete(directory, true); }
    [Fact]
    public async Task SriUnavailableRetriesAreBoundedAndReportedAccurately()
    {
        var repository = new JsonStateRepository(Path.Combine(directory, "retry")); var storage = new DocumentStorage(Path.Combine(directory, "out"));
        repository.Update(s => { s.Accounts.Add(new("a", "Cuenta")); return Validation.Enqueue(s, new("a", new(2026, 9, 12), new(2026, 9, 12), ["Facturas"])); });
        var processor = new JobProcessor(repository, storage, new FixtureSri(() => throw new SriUnavailableException("Fixture sin conexión.")));
        for (var i = 0; i < 3; i++) await processor.Tick(CancellationToken.None);
        var job = repository.Read(s => s.Executions.Single()); Assert.Equal("Failed", job.Status); Assert.Equal(3, job.Attempts); Assert.Contains("SRI no respondió", job.Message);
    }
    [Fact]
    public async Task HumanSuspensionStillProducesMonthlyReportForStoredDownloads()
    {
        var repository = new JsonStateRepository(Path.Combine(directory, "partial")); var storage = new DocumentStorage(Path.Combine(directory, "out"));
        repository.Update(s => { s.Accounts.Add(new("a", "Cuenta", "1790000000001")); return Validation.Enqueue(s, new("a", new(2026, 9, 12), new(2026, 9, 12), ["Facturas"])); });
        var pipeline = new DownloadPipeline(repository, storage, new XmlDocumentParser());
        var processor = new JobProcessor(repository, storage, new FixtureSri(() =>
        {
            pipeline.Process(new("a", "Facturas", Key, new(2026, 9, 12), Xml(), null));
            throw new HumanActionRequiredException("Fixture solicita intervención.");
        }));
        await processor.Tick(CancellationToken.None);
        Assert.Equal("RequiresHumanAction", repository.Read(s => s.Executions.Single().Status));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "out"), "*.xlsx", SearchOption.AllDirectories));
    }
    [Fact]
    public void PdfOnlyIsStoredAsPartialWithoutInventingXmlFields()
    {
        var repository = new JsonStateRepository(Path.Combine(directory, "pdf")); var storage = new DocumentStorage(Path.Combine(directory, "out"));
        repository.Update(s => { s.Accounts.Add(new("a", "Cuenta", "1790000000001")); return true; });
        var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + Key, "FECHA DE EMISION: 12/09/2026", "RECEPTOR: FICTICIO", "IMPORTE TOTAL: 11.50"]);
        var d = new DownloadPipeline(repository, storage, new XmlDocumentParser()).Process(new("a", "Facturas", Key, new(2026, 9, 12), null, pdf));
        Assert.Null(d.XmlPath); Assert.Null(d.Authorization); Assert.NotNull(d.PdfSha256); Assert.Equal("Revisar", d.Status); Assert.Equal(11.50m, d.Total);
        Assert.Contains("09-Septiembre", d.PdfPath);
    }
    private sealed class FixtureSri(Action action) : ISriComprobanteService
    { public Task Execute(Query query, CancellationToken cancellation) { action(); return Task.CompletedTask; } }
}
