using System.Text.Json;
using ClosedXML.Excel;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class AccountingReconciliationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sri-reconcile-" + Guid.NewGuid().ToString("N"));
    private const string Key = PhaseTwoTests.Key;
    private static readonly DateOnly Issued = new(2026, 9, 12);
    private readonly JsonStateRepository repository;
    private readonly DocumentStorage storage;
    private readonly DownloadPipeline pipeline;

    public AccountingReconciliationTests()
    {
        repository = new JsonStateRepository(Path.Combine(root, "state"));
        storage = new DocumentStorage(Path.Combine(root, "files"));
        repository.Update(s => { s.Accounts.Add(new("a", "Prueba", "1790000000001")); return true; });
        pipeline = new DownloadPipeline(repository, storage, new XmlDocumentParser());
    }

    private static byte[] Pdf(string total = "11.50") => PdfFixture.BuildLines([
        "CLAVE DE ACCESO: " + Key, "RUC: 1790000000001", "RAZON SOCIAL EMISOR: EMISOR FICTICIO",
        "FACTURA: 001-001-000000001", "FECHA DE EMISION: 12/09/2026", "RECEPTOR: RECEPTOR FICTICIO",
        "IDENTIFICACION: 0012345678", "SUBTOTAL SIN IMPUESTOS: 10.00", "TOTAL DESCUENTO: 0.00",
        "TOTAL IMPUESTOS: 1.50", "IMPORTE TOTAL: " + total]);

    [Fact]
    public void PdfOnlyKeepsLabeledValuesAndIncompleteDocumentIsOneReview()
    {
        repository.Update(s => { s.Executions.Add(new Execution { Id = "job", Status = "Running", Query = new("a", Issued, Issued, ["Facturas"]), AttemptedKeys = [Key] }); return true; });
        var doc = pipeline.Process(new("a", "Facturas", Key, Issued, null, Pdf()));
        Assert.Equal("Revisar", doc.Status); Assert.Equal("0012345678", doc.Identification);
        Assert.Equal("EMISOR FICTICIO", doc.IssuerName); Assert.Equal(0m, doc.Discount);
        Assert.Equal(11.50m, doc.Total); Assert.Equal("PDF", doc.FieldEvidence["Total"].Source);
        Assert.Null(doc.Authorization);
        var job = repository.Read(s => s.Executions.Single()); Assert.Equal(1, job.Reviews); Assert.Equal(0, job.Errors);
        var bookPath = storage.Report("a", 2026, 9, [doc, doc]);
        using var book = new XLWorkbook(storage.Resolve(bookPath)); var summary = book.Worksheet("Resumen");
        Assert.Equal(2, summary.LastRowUsed()!.RowNumber()); Assert.Equal(XLDataType.Text, summary.Cell(2, 6).DataType);
        Assert.Equal(XLDataType.Number, summary.Cell(2, 10).DataType); Assert.Equal(0m, summary.Cell(2, 13).GetValue<decimal>());
    }

    [Theory]
    [InlineData("11.50", false)]
    [InlineData("99.00", true)]
    public void XmlAndPdfAreComparedWithoutOverwritingXml(string pdfTotal, bool conflict)
    {
        var doc = pipeline.Process(new("a", "Facturas", Key, Issued, PhaseTwoTests.Xml(), Pdf(pdfTotal)));
        Assert.Equal(11.50m, doc.Total); Assert.Equal("XML", doc.FieldEvidence["Total"].Source);
        Assert.Equal("PDF", doc.FieldEvidence["Descuento"].Source); Assert.Equal(0m, doc.Discount);
        Assert.Equal(conflict, doc.Warning?.Contains("conciliación") == true);
        if (conflict) Assert.Equal("Revisar", doc.Status);
    }

    [Fact]
    public void ExistingStructuredValuesTakePriorityOverPdf()
    {
        pipeline.Process(new("a", "Facturas", Key, Issued, PhaseTwoTests.Xml(), null));
        var doc = pipeline.Process(new("a", "Facturas", Key, Issued, null, Pdf("99.00")));
        Assert.Equal(11.50m, doc.Total); Assert.Contains("conciliación", doc.Warning);
        Assert.NotNull(doc.XmlPath); Assert.Equal("XML", doc.FieldEvidence["Total"].Source);
    }

    [Fact]
    public void IdentityMismatchIsRejectedRatherThanDowngradedToReview()
    {
        var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + Key, "FACTURA: 002-001-000000001"]);
        Assert.Throws<ArgumentException>(() => pipeline.Process(new("a", "Facturas", Key, Issued, PhaseTwoTests.Xml(), pdf)));
        Assert.Empty(repository.Read(s => s.Documents));
    }

    [Fact]
    public void MissingLabelsRemainUnknownAndDuplicateObservationsCountOnce()
    {
        var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + Key]);
        var doc = pipeline.Process(new("a", "Facturas", Key, Issued, null, pdf));
        Assert.Null(doc.Subtotal); Assert.Null(doc.Discount); Assert.Null(doc.Taxes); Assert.Null(doc.Total);
        var state = new State();
        var job = new Execution { Query = new("a", Issued, Issued, ["Facturas"], "Stored") };
        state.Documents.Add(doc); state.Documents.Add(doc);
        ReviewCounter.Update(state, job); Assert.Equal(1, job.Reviews);
    }

    [Fact]
    public void OldAgentPayloadLeavesReviewCountAndRealQueryErrorsArePreserved()
    {
        var state = new State();
        var job = new Execution { Id = "job", Status = "Running", Query = new("a", Issued, Issued, ["Facturas"]), AttemptedKeys = [Key] };
        state.Executions.Add(job); state.Documents.Add(new Voucher { Account = "a", Type = "Facturas", AccessKey = Key, Issued = Issued, PdfPath = "stored.pdf", Status = "Revisar", Warning = "Faltan datos." });
        var oldJson = JsonSerializer.Serialize(new State { Executions = [job] }).Replace("\"Reviews\":0,", "");
        var oldAgent = JsonSerializer.Deserialize<State>(oldJson)!;
        oldAgent.Executions[0].Queries = [new(Issued, "Facturas", 1, 1, 1, "ConErrores")];
        NativeExecution.ApplyRemote(state, "job", oldAgent);
        Assert.Equal(1, job.Reviews); Assert.Equal(1, job.Errors);
        Assert.Contains("\"Reviews\":1", JsonSerializer.Serialize(job));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
