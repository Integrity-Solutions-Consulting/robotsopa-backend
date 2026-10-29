using System.Text;
using ClosedXML.Excel;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sri-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string account = "1790000000001";
    private JsonStateRepository Repository => new(Path.Combine(directory, "state"));
    private DocumentStorage Storage => new(Path.Combine(directory, "downloads"));
    private Query Query(string mode = "Stored") => new(account, new(2025, 10, 1), new(2026, 9, 30), ["Facturas"], mode);
    private static byte[] Fixture(string root = "factura", string info = "infoFactura", string recipientTag = "razonSocialComprador", string idTag = "identificacionComprador", string totalTag = "importeTotal") => Encoding.UTF8.GetBytes($"""
      <autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>0000123456789</numeroAutorizacion><fechaAutorizacion>2026-10-01T10:30:00-05:00</fechaAutorizacion><comprobante><![CDATA[
      <{root}><infoTributaria><razonSocial>EMISOR FICTICIO</razonSocial><ruc>1790000000001</ruc><claveAcceso>0109202601179000000000110010010000000011234567811</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria><{info}><fechaEmision>12/09/2026</fechaEmision><{recipientTag}>RECEPTOR FICTICIO</{recipientTag}><{idTag}>0012345678</{idTag}><totalSinImpuestos>10.00</totalSinImpuestos><totalConImpuestos><totalImpuesto><valor>1.50</valor></totalImpuesto></totalConImpuestos><{totalTag}>11.50</{totalTag}></{info}></{root}>
      ]]></comprobante></autorizacion>
      """);
    public CoreTests() { Directory.CreateDirectory(directory); Repository.Update(s => { s.Accounts.Add(new(account, "Cuenta de pruebas")); return true; }); }

    [Fact]
    public void XmlDistinguishesRecipientAndAuthorization()
    {
        var d = new XmlDocumentParser().ParseXml(account, Fixture());
        Assert.Equal("RECEPTOR FICTICIO", d.Recipient); Assert.Equal("0012345678", d.Identification);
        Assert.Equal(new DateOnly(2026, 9, 12), d.Issued); Assert.StartsWith("2026-10-01", d.AuthorizedAt);
        Assert.Equal(11.50m, d.Total); Assert.Equal("0000123456789", d.Authorization); Assert.Equal("ValidadoXML", d.Status);
    }
    [Fact]
    public void CreditNoteUsesModificationAmount()
    { var d = new XmlDocumentParser().ParseXml(account, Fixture("notaCredito", "infoNotaCredito", totalTag: "valorModificacion")); Assert.Equal("Notas-Credito", d.Type); Assert.Equal(11.50m, d.Total); }
    [Fact]
    public void RejectsXmlFromADifferentRegisteredIssuer()
    {
        var d = new XmlDocumentParser().ParseXml(account, Fixture());
        Validation.DocumentAccount(new Account(account, "Correcta"), d);
        Assert.Throws<ArgumentException>(() => Validation.DocumentAccount(new Account("otra", "Otra", "9999999999001"), d));
    }
    [Fact]
    public void RetentionDoesNotInventInvoiceTotal()
    { var d = new XmlDocumentParser().ParseXml(account, Fixture("comprobanteRetencion", "infoCompRetencion", "razonSocialSujetoRetenido", "identificacionSujetoRetenido")); Assert.Equal("Retenciones", d.Type); Assert.Equal("RECEPTOR FICTICIO", d.Recipient); Assert.Null(d.Total); Assert.Equal("Revisar", d.Status); }
    [Fact]
    public void RejectsXmlExternalEntities()
    { Assert.ThrowsAny<Exception>(() => new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><factura>&secret;</factura>"))); }
    [Fact]
    public void ValidatesRangesAndDuplicateJobs()
    {
        Repository.Update(s => { Assert.Throws<ArgumentException>(() => Validation.Enqueue(s, Query() with { To = new(2023, 1, 1) })); Validation.Enqueue(s, Query()); Assert.Throws<ArgumentException>(() => Validation.Enqueue(s, Query())); return true; });
    }
    [Fact]
    public void MissingSubtotalIsFlaggedAndTransportDateIsNotEmissionDate()
    {
        var xml = Encoding.UTF8.GetString(Fixture()).Replace("<totalSinImpuestos>10.00</totalSinImpuestos>", "");
        var d = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        Assert.Null(d.Subtotal); Assert.Equal("Revisar", d.Status);
        var transportDateOnly = xml.Replace("fechaEmision", "fechaIniTransporte");
        Assert.Throws<ArgumentException>(() => new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(transportDateOnly)));
    }
    [Fact]
    public async Task JsonTransactionsDoNotLoseConcurrentUpdates()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => Repository.Update(s => { s.Accounts.Add(new("account-" + i, "Concurrente")); return true; }))));
        Assert.Equal(25, Repository.Read(s => s.Accounts.Count));
    }
    [Fact]
    public void RecoversValidBackupWithoutResettingState()
    {
        Repository.Update(s => { s.Configurations.Add(Query()); return true; });
        File.WriteAllText(Path.Combine(directory, "state", "state.json"), "{corrupt");
        Assert.Single(Repository.Read(s => s.Accounts));
    }
    [Fact]
    public void StorageUsesEmissionMonthAndPreventsCollisionsAndTraversal()
    {
        Storage.CheckAccess(); var d = new XmlDocumentParser().ParseXml(account, Fixture());
        var path = Storage.Save(d, "xml", Fixture());
        Assert.Contains(Path.Combine("2026", "09-Septiembre", account, "Facturas"), path);
        Assert.Equal(path, Storage.Save(d, "xml", Fixture()));
        Assert.Throws<ArgumentException>(() => Storage.Save(d, "xml", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture()).Replace("11.50", "12.50"))));
        Assert.Throws<ArgumentException>(() => Storage.Resolve("../escape"));
        Assert.Throws<ArgumentException>(() => Storage.Save(d, "pdf", "not a pdf"u8.ToArray()));
    }
    [Fact]
    public void ExcelHasThreeSheetsAndNumericAmountsWithoutMixedAccounts()
    {
        var d = new XmlDocumentParser().ParseXml(account, Fixture());
        var other = new XmlDocumentParser().ParseXml("otra", Fixture());
        var path = Storage.Report(account, 2026, 9, [d, d, other]);
        Storage.Report(account, 2026, 9, [d]); // Regeneración atómica.
        using var book = new XLWorkbook(Storage.Resolve(path));
        Assert.Equal(3, book.Worksheets.Count); var sheet = book.Worksheet("Documentos");
        Assert.Equal(2, sheet.LastRowUsed()!.RowNumber()); Assert.Equal(8, sheet.LastColumnUsed()!.ColumnNumber()); Assert.Equal(XLDataType.Number, sheet.Cell(2, 8).DataType);
        Assert.Equal("0012345678", sheet.Cell(2, 5).GetString()); Assert.Equal(11.50m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.DoesNotContain(sheet.Row(1).CellsUsed(), c => c.GetString() is "Impuestos" or "Clave de acceso" or "Ruta PDF" or "Ruta XML" or "Estado de extracción" or "Fecha y hora de autorización");
        Assert.Equal(1, sheet.SheetView.SplitRow); Assert.True(sheet.AutoFilter.IsEnabled);
    }
    [Fact]
    public void ExcelKeepsStandardDocumentRowHeightAndWrapsReviewMessages()
    {
        var d = new XmlDocumentParser().ParseXml(account, Fixture());
        d.Recipient = string.Join(" ", Enumerable.Repeat("RECEPTOR FICTICIO", 10));
        d.Status = "Revisar"; d.Warning = string.Join(" ", Enumerable.Repeat("Dato pendiente de revisión", 10));
        using var book = new XLWorkbook(Storage.Resolve(Storage.Report(account, 2026, 9, [d])));
        Assert.False(book.Worksheet("Documentos").Cell(2, 4).Style.Alignment.WrapText);
        Assert.Equal(20, book.Worksheet("Documentos").Row(2).Height);
        Assert.True(book.Worksheet("Pendientes").Row(2).Height > 20);
        Assert.Equal("Número de comprobante", book.Worksheet("Pendientes").Cell(1, 1).GetString());
    }
    [Fact]
    public async Task StoredJobGeneratesExcelAndPersistsCheckpoint()
    {
        Repository.Update(s => { s.Documents.Add(new XmlDocumentParser().ParseXml(account, Fixture())); Validation.Enqueue(s, Query()); return true; });
        Assert.True(await new JobProcessor(Repository, Storage, new SriPortal()).Tick(CancellationToken.None));
        var job = Repository.Read(s => s.Executions.Single()); Assert.Equal("Completed", job.Status); Assert.Equal("2026-09", job.Checkpoint);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "downloads"), "*.xlsx", SearchOption.AllDirectories));
    }
    [Fact]
    public void RebuildsCatalogFromStoredXmlAndPreservesPdfWarning()
    {
        var d = new XmlDocumentParser().ParseXml(account, Fixture());
        Storage.Save(d, "xml", Fixture()); Storage.Save(d, "pdf", PdfFixture.Build("999999"));
        new DocumentIndexer(Repository, Storage, new XmlDocumentParser()).Rebuild(account);
        var indexed = Repository.Read(s => s.Documents.Single());
        Assert.Equal(d.AccessKey, indexed.AccessKey); Assert.NotNull(indexed.XmlPath); Assert.NotNull(indexed.PdfPath); Assert.Equal("Revisar", indexed.Status);
        new DocumentIndexer(Repository, Storage, new XmlDocumentParser()).Rebuild(account);
        Assert.Single(Repository.Read(s => s.Documents));
    }
    [Fact]
    public async Task SriJobRequiresHumanActionInsteadOfFakeSuccess()
    {
        Repository.Update(s => Validation.Enqueue(s, Query("Sri") with { From=new(2026,9,12),To=new(2026,9,12) }));
        await new JobProcessor(Repository, Storage, new SriPortal()).Tick(CancellationToken.None);
        Assert.Equal("RequiresHumanAction", Repository.Read(s => s.Executions.Single().Status));
    }
    [Fact]
    public async Task PendingCancellationAndRestartRecovery()
    {
        Repository.Update(s => { var e = Validation.Enqueue(s, Query()); e.Status = "Running"; e.Attempts = 1; e.Checkpoint = "2026-08"; return true; });
        var processor = new JobProcessor(Repository, Storage, new SriPortal()); processor.Recover();
        Assert.Equal("Pending", Repository.Read(s => s.Executions.Single().Status));
        Assert.Equal("2026-08", Repository.Read(s => s.Executions.Single().Checkpoint));
        Repository.Update(s => { s.Executions.Single().CancelRequested = true; return true; });
        await processor.Tick(CancellationToken.None); Assert.Equal("Cancelled", Repository.Read(s => s.Executions.Single().Status));
    }
    [Theory]
    [InlineData("Daily", 28)]
    [InlineData("Weekly", 3)]
    [InlineData("Monthly", 27)]
    public void SchedulerUsesGuayaquil(string frequency, int day)
    { var next = Validation.Next(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(-5)), frequency); Assert.Equal(day, next.Day); Assert.Equal(TimeSpan.FromHours(-5), next.Offset); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
