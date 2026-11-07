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
    public void LegacyIndexedXmlIsUsedToRestoreReportDetails()
    {
        var xml = Fixture();
        var document = new XmlDocumentParser().ParseXml(account, xml);
        document.XmlPath = Storage.Save(document, "xml", xml);
        document.XmlSha256 = DownloadPipeline.Hash(xml);
        document.XmlData = null;
        document.Total = null;
        var path = Storage.Resolve(Storage.Report(account, 2026, 9, [document]));
        using var book = new XLWorkbook(path);
        Assert.Equal(11.50m, book.Worksheet("Facturas").Cell(2, 11).GetValue<decimal>());
        Assert.Empty(XmlExcelComparator.Compare(xml, path));
    }
    [Fact]
    public void ParsesNamespacedEnvelopeAndDocument()
    {
        var xml = Encoding.UTF8.GetString(Fixture()).Replace("<autorizacion>", "<autorizacion xmlns=\"urn:authorization\">")
            .Replace("<factura>", "<factura xmlns=\"urn:invoice\">").Replace("12/09/2026", "2026-09-12");
        var document = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        Assert.Equal(new DateOnly(2026, 9, 12), document.Issued);
        Assert.Equal(1.50m, document.Taxes);
        Assert.Equal("0000123456789", document.Authorization);
    }
    [Fact]
    public void ParserRespectsXmlEncodingDeclaration()
    {
        var xml = Encoding.UTF8.GetString(Fixture()).Replace("EMISOR FICTICIO", "EMISOR MUÑOZ");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\" encoding=\"utf-16\"?>" + xml)).ToArray();
        var document = new XmlDocumentParser().ParseXml(account, bytes);
        Assert.Contains(document.XmlData!.Fields, field => field.Value == "EMISOR MUÑOZ");
    }
    [Fact]
    public void GuideUsesAccessKeyDateOnlyWhenEmissionDateIsAbsent()
    {
        var xml = Encoding.UTF8.GetString(Fixture("guiaRemision", "infoGuiaRemision"))
            .Replace("<fechaEmision>12/09/2026</fechaEmision>", "<fechaIniTransporte>12/09/2026</fechaIniTransporte>");
        var document = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        Assert.Equal(new DateOnly(2026, 9, 1), document.Issued);
        Assert.Contains("clave de acceso", document.Warning);
    }
    [Theory]
    [InlineData("factura", "infoFactura", "importeTotal", "Facturas")]
    [InlineData("notaCredito", "infoNotaCredito", "valorModificacion", "Notas crédito")]
    [InlineData("notaDebito", "infoNotaDebito", "valorTotal", "Notas débito")]
    [InlineData("liquidacionCompra", "infoLiquidacionCompra", "importeTotal", "Liquidaciones")]
    [InlineData("guiaRemision", "infoGuiaRemision", "importeTotal", "Guías remisión")]
    public void TypeSummaryReopensWithCorrectDateAndTextIdentifiers(string root, string info, string totalTag, string sheetName)
    {
        var xml = Fixture(root, info, totalTag: totalTag);
        var document = new XmlDocumentParser().ParseXml(account, xml);
        using var book = new XLWorkbook(Storage.Resolve(Storage.Report(account, 2026, 9, [document])));
        var sheet = book.Worksheet(sheetName);
        Assert.Equal(new DateTime(2026, 9, 12), sheet.Cell(2, 1).GetDateTime());
        Assert.Equal(document.AccessKey, sheet.Cell(2, 3).GetString());
        Assert.Equal("0000123456789", sheet.Cell(2, 4).GetString());
    }
    [Theory]
    [InlineData("factura", "infoFactura", "importeTotal")]
    [InlineData("comprobanteRetencion", "infoCompRetencion", "importeTotal")]
    [InlineData("notaCredito", "infoNotaCredito", "valorModificacion")]
    [InlineData("notaDebito", "infoNotaDebito", "valorTotal")]
    [InlineData("liquidacionCompra", "infoLiquidacionCompra", "importeTotal")]
    [InlineData("guiaRemision", "infoGuiaRemision", "importeTotal")]
    public void ComparatorFindsChangesForEverySupportedType(string root, string info, string totalTag)
    {
        var xml = Fixture(root, info, totalTag: totalTag);
        var voucher = new XmlDocumentParser().ParseXml(account, xml);
        var path = Storage.Resolve(Storage.Report(account, 2026, 9, [voucher]));
        var coverage = XmlExcelComparator.CompareDetailed(xml, path);
        Assert.Empty(coverage.Differences);
        Assert.NotEmpty(coverage.Verified); Assert.NotEmpty(coverage.NotVerified);
        using (var book = new XLWorkbook(path))
        {
            book.Worksheet("Documentos").Cell(2, 6).Value = "alterado";
            book.Save();
        }
        var differences = XmlExcelComparator.Compare(xml, path);
        Assert.Contains(differences, d => d.Field == "Identificación" && d.Kind == "incorrecto" && d.Actual == "[presente]");
    }
    [Fact]
    public void ComparatorDetectsWrongAuthorizationDateAndSpecificCreditNoteField()
    {
        var xml = Encoding.UTF8.GetString(Fixture("notaCredito", "infoNotaCredito", totalTag: "valorModificacion"))
            .Replace("</infoNotaCredito>", "<numDocModificado>001-001-000000123</numDocModificado></infoNotaCredito>");
        var bytes = Encoding.UTF8.GetBytes(xml);
        var voucher = new XmlDocumentParser().ParseXml(account, bytes);
        var path = Storage.Resolve(Storage.Report(account, 2026, 9, [voucher]));
        using (var book = new XLWorkbook(path))
        {
            book.Worksheet("Documentos").Cell(2, 4).Value = new DateTime(2026, 10, 2);
            book.Worksheet("Notas crédito").Cell(2, 13).Value = "distinto";
            book.Save();
        }
        var differences = XmlExcelComparator.Compare(bytes, path);
        Assert.Contains(differences, d => d.Field == "Fecha de autorización" && d.Kind == "incorrecto");
        Assert.Contains(differences, d => d.Field == "Notas crédito/numDocModificado" && d.Kind == "incorrecto");
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
        Assert.Equal(6, book.Worksheets.Count); var sheet = book.Worksheet("Documentos");
        Assert.Equal(2, sheet.LastRowUsed()!.RowNumber()); Assert.Equal(10, sheet.LastColumnUsed()!.ColumnNumber()); Assert.Equal(XLDataType.Number, sheet.Cell(2, 8).DataType);
        Assert.Equal("Fecha", sheet.Cell(1, 9).GetString()); Assert.Equal(new DateTime(2026, 9, 12), sheet.Cell(2, 9).GetDateTime());
        Assert.Equal("Hora", sheet.Cell(1, 10).GetString()); Assert.Equal(new TimeSpan(10, 30, 0), sheet.Cell(2, 10).GetTimeSpan());
        Assert.Equal("0012345678", sheet.Cell(2, 6).GetString()); Assert.Equal(11.50m, sheet.Cell(2, 8).GetValue<decimal>());
        var invoice = book.Worksheet("Facturas");
        Assert.Equal("EMISOR FICTICIO", invoice.Cell(2, 6).GetString());
        Assert.Equal(1.50m, invoice.Cell(2, 10).GetValue<decimal>());
        Assert.Equal(1.50m, book.Worksheet("Detalles facturas").Cell(2, 14).GetValue<decimal>());
        Assert.DoesNotContain(sheet.Row(1).CellsUsed(), c => c.GetString() is "Clave de acceso" or "Ruta PDF" or "Ruta XML" or "Estado de extracción");
        Assert.Equal(1, sheet.SheetView.SplitRow); Assert.True(sheet.AutoFilter.IsEnabled);
    }
    [Fact]
    public void ReportPublicationVersionsPriorFileAndPreservesItOnFailure()
    {
        var document = new XmlDocumentParser().ParseXml(account, Fixture());
        var relative = Storage.Report(account, 2026, 9, [document]);
        var path = Storage.Resolve(relative);
        var first = File.ReadAllBytes(path);
        document.Total = 99m;
        Storage.Report(account, 2026, 9, [document]);
        var versionDirectory = Path.Combine(Path.GetDirectoryName(path)!, ".versiones");
        var backup = Assert.Single(Directory.GetFiles(versionDirectory, "*.xlsx"));
        Assert.Equal(first, File.ReadAllBytes(backup));
        var current = File.ReadAllBytes(path);
        document.Recipient = new string('x', 40000);
        Assert.ThrowsAny<Exception>(() => Storage.Report(account, 2026, 9, [document]));
        Assert.Equal(current, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(versionDirectory, "*.xlsx"));
        using var check = new XLWorkbook(path);
        Assert.Equal(99m, check.Worksheet("Documentos").Cell(2, 8).GetValue<decimal>());
    }
    [Fact]
    public void ReportPublicationRejectsConcurrentWriterWithoutChangingFile()
    {
        var document = new XmlDocumentParser().ParseXml(account, Fixture());
        var path = Storage.Resolve(Storage.Report(account, 2026, 9, [document]));
        var before = File.ReadAllBytes(path);
        var lockPath = Storage.Resolve(Path.Combine("2026", ".report-" + account + ".lock"));
        using (var gate = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => Storage.Report(account, 2026, 9, [document]));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public void RetentionDetailsSurviveParsingAndExcelRoundTrip()
    {
        var xml = Encoding.UTF8.GetString(Fixture("comprobanteRetencion", "infoCompRetencion", "razonSocialSujetoRetenido", "identificacionSujetoRetenido"))
            .Replace("</comprobanteRetencion>", "<impuestos><impuesto><codigo>1</codigo><codigoRetencion>303</codigoRetencion><baseImponible>100.00</baseImponible><porcentajeRetener>2.75</porcentajeRetener><valorRetenido>2.75</valorRetenido><numDocSustento>001001000000001</numDocSustento></impuesto><impuesto><codigo>2</codigo><codigoRetencion>1</codigoRetencion><baseImponible>15.00</baseImponible><porcentajeRetener>30</porcentajeRetener><valorRetenido>4.50</valorRetenido></impuesto></impuestos></comprobanteRetencion>");
        var doc = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        Assert.Equal(2, doc.XmlData!.Retentions.Count); Assert.Equal(7.25m, doc.Total);
        using var book = new XLWorkbook(Storage.Resolve(Storage.Report(account, 2026, 9, [doc])));
        var sheet = book.Worksheet("Retenciones");
        Assert.Equal(100m, sheet.Cell(2, 3).GetValue<decimal>()); Assert.Equal(2.75m, sheet.Cell(2, 4).GetValue<decimal>());
        Assert.Equal(4.5m, sheet.Cell(3, 8).GetValue<decimal>()); Assert.Equal(7.25m, sheet.Cell(2, 9).GetValue<decimal>());
        Assert.True(sheet.Cell(3, 9).IsEmpty()); Assert.Equal("001001000000001", sheet.Cell(2, 12).GetString());
        Assert.Contains(book.Worksheet("XML Retenciones").Column(3).CellsUsed(), c => c.GetString().Contains("impuesto[2]"));
        var summary = book.Worksheet("Resumen retenciones");
        Assert.StartsWith("SUBTOTAL(109", summary.Cell(2, 3).FormulaA1);
        Assert.StartsWith("SUMIF(", summary.Cell(5, 3).FormulaA1);
        Assert.Equal(2.75m, summary.Cell(2, 3).GetValue<decimal>());
        Assert.Equal(4.50m, summary.Cell(3, 3).GetValue<decimal>());
        Assert.Equal(2.75m, summary.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(4.50m, summary.Cell(6, 3).GetValue<decimal>());
        Assert.Empty(XmlExcelComparator.Compare(Encoding.UTF8.GetBytes(xml), Storage.Resolve(Storage.Report(account, 2026, 9, [doc]))));
    }
    [Fact]
    public void AtsRetentionKeepsSupportForEachLine()
    {
        var xml = Encoding.UTF8.GetString(Fixture("comprobanteRetencion", "infoCompRetencion", "razonSocialSujetoRetenido", "identificacionSujetoRetenido"))
            .Replace("</comprobanteRetencion>", "<docsSustento><docSustento><codDocSustento>01</codDocSustento><numDocSustento>001001000000099</numDocSustento><fechaEmisionDocSustento>01/09/2026</fechaEmisionDocSustento><retenciones><retencion><codigo>2</codigo><codigoRetencion>2</codigoRetencion><baseImponible>100.00</baseImponible><porcentajeRetener>70</porcentajeRetener><valorRetenido>70.00</valorRetenido></retencion></retenciones></docSustento></docsSustento></comprobanteRetencion>");
        var doc = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        var retention = Assert.Single(doc.XmlData!.Retentions);
        Assert.Equal("001001000000099", retention.SupportNumber); Assert.Equal(70m, retention.Amount);
        using var book = new XLWorkbook(Storage.Resolve(Storage.Report(account, 2026, 9, [doc])));
        Assert.Equal(100m, book.Worksheet("Retenciones").Cell(2, 6).GetValue<decimal>());
        Assert.Equal("001001000000099", book.Worksheet("Retenciones").Cell(2, 12).GetString());
        Assert.Equal(new DateTime(2026, 9, 1), book.Worksheet("Retenciones").Cell(2, 23).GetDateTime());
    }
    [Fact]
    public void DebitNotePreservesEachReasonAndAmount()
    {
        var xml = Encoding.UTF8.GetString(Fixture("notaDebito", "infoNotaDebito", totalTag: "valorTotal"))
            .Replace("</notaDebito>", "<motivos><motivo><razon>Interés</razon><valor>2.10</valor></motivo><motivo><razon>Servicio</razon><valor>3.20</valor></motivo></motivos></notaDebito>");
        var doc = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        using var book = new XLWorkbook(Storage.Resolve(Storage.Report(account, 2026, 9, [doc])));
        var sheet = book.Worksheet("Detalles notas débito");
        var rows = sheet.RowsUsed().Where(row => row.Cell(3).GetString() == "motivo").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(2.10m, rows[0].Cell(21).GetValue<decimal>());
        Assert.Equal(3.20m, rows[1].Cell(21).GetValue<decimal>());
    }
    [Fact]
    public void ParserPreservesTypedDetailsTaxesAndRelatedDocument()
    {
        var xml = Encoding.UTF8.GetString(Fixture("notaCredito", "infoNotaCredito", totalTag: "valorModificacion"))
            .Replace("</infoNotaCredito>", "<codDocModificado>01</codDocModificado><numDocModificado>001-001-000000123</numDocModificado></infoNotaCredito>")
            .Replace("</notaCredito>", "<detalles><detalle><codigoInterno>0001</codigoInterno><descripcion>Servicio</descripcion><cantidad>2.50</cantidad><precioUnitario>4.00</precioUnitario><descuento>0.50</descuento><precioTotalSinImpuesto>9.50</precioTotalSinImpuesto><impuestos><impuesto><codigo>2</codigo><codigoPorcentaje>4</codigoPorcentaje><baseImponible>9.50</baseImponible><tarifa>15</tarifa><valor>1.425</valor></impuesto></impuestos></detalle></detalles></notaCredito>");
        var voucher = new XmlDocumentParser().ParseXml(account, Encoding.UTF8.GetBytes(xml));
        var line = Assert.Single(voucher.XmlData!.Lines);
        Assert.Equal("0001", line.Code); Assert.Equal(2.50m, line.Quantity); Assert.Equal(9.50m, line.NetAmount);
        var tax = Assert.Single(voucher.XmlData.TaxLines, t => t.Code == "2");
        Assert.Equal(1.425m, tax.Amount); Assert.StartsWith(line.Path, tax.Path);
        Assert.Equal("001-001-000000123", Assert.Single(voucher.XmlData.RelatedDocuments).Number);
    }
    [Fact]
    public void ExcelWrapsLongNamesAndReviewMessages()
    {
        var d = new XmlDocumentParser().ParseXml(account, Fixture());
        d.Recipient = string.Join(" ", Enumerable.Repeat("RECEPTOR FICTICIO", 10));
        d.Status = "Revisar"; d.Warning = string.Join(" ", Enumerable.Repeat("Dato pendiente de revisión", 10));
        using var book = new XLWorkbook(Storage.Resolve(Storage.Report(account, 2026, 9, [d])));
        Assert.True(book.Worksheet("Documentos").Cell(2, 5).Style.Alignment.WrapText);
        Assert.True(book.Worksheet("Documentos").Row(2).Height > 20);
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
    public void ReindexKeepsNumericMonthAndOriginalFiles()
    {
        var relative = Path.Combine(account, "2026", "09", "Facturas", "original.xml");
        var path = Storage.Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Fixture());
        new DocumentIndexer(Repository, Storage, new XmlDocumentParser()).Rebuild(account);
        var indexed = Repository.Read(s => s.Documents.Single());
        Assert.Equal(relative, indexed.XmlPath); Assert.Equal(Fixture(), File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Storage.Resolve(Path.Combine("2026", "09-Septiembre"))));
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
