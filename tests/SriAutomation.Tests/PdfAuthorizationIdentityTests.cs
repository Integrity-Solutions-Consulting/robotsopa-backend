using SriAutomation.Domain;
using SriAutomation.Application;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class PdfAuthorizationIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sri-pdf-identity-" + Guid.NewGuid().ToString("N"));
    private const string Key = PhaseTwoTests.Key;
    private static readonly DateOnly Date = new(2026, 9, 12);

    private (JsonStateRepository Repository, DownloadPipeline Pipeline) Setup()
    {
        Directory.CreateDirectory(directory);
        var repository = new JsonStateRepository(Path.Combine(directory, "state"));
        var storage = new DocumentStorage(Path.Combine(directory, "out"));
        repository.Update(s => { s.Accounts.Add(new("a", "Cuenta", "1790000000001")); return true; });
        return (repository, new DownloadPipeline(repository, storage, new XmlDocumentParser()));
    }

    [Fact]
    public void AuthorizationOnFollowingLineMatchesAndRetryIsIdempotent()
    {
        var (repository, pipeline) = Setup();
        var pdf = PdfFixture.BuildLines(["NUMERO DE AUTORIZACION", Key, "FECHA DE AUTORIZACION", "CLAVE DE ACCESO: GRAFICO"]);
        var file = new DownloadedVoucher("a", "Facturas", Key, Date, null, pdf);
        var first = pipeline.Process(file); var again = pipeline.Process(file);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(DownloadPipeline.Hash(pdf), again.PdfSha256);
        Assert.True(pipeline.IsAvailable(again, "pdf"));
        Assert.Single(repository.Read(s => s.Documents));
        Assert.Throws<ArgumentException>(() => pipeline.Process(file with { Key = new string('9', 49) }));
    }

    [Fact]
    public void AmbiguousOrConflictingAuthorizationIsRejected()
    {
        var other = new string('9', 49);
        var cases = new[] {
            PdfFixture.BuildLines(["NUMERO DE AUTORIZACION", Key, "NUMERO DE AUTORIZACION", Key]),
            PdfFixture.BuildLines(["NUMERO DE AUTORIZACION", Key, "CLAVE DE ACCESO: " + other]),
            PdfFixture.BuildLines(["NUMERO DE AUTORIZACION", Key, "OTRO IDENTIFICADOR: " + other]),
            PdfFixture.BuildLines(["NUMERO DE AUTORIZACION", Key[..48]]),
            PdfFixture.BuildLines(["SIN IDENTIDAD VERIFICABLE"]),
            new byte[] { 1, 2, 3 }
        };
        var (repository, pipeline) = Setup();
        foreach (var pdf in cases)
            Assert.Throws<ArgumentException>(() => pipeline.Process(new("a", "Facturas", Key, Date, null, pdf)));
        Assert.Empty(repository.Read(s => s.Documents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitAccessKeyStillMatches(bool separated)
    {
        var representation = separated ? string.Join(" ", Key.Select(c => c.ToString())) : Key;
        var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + representation]);
        Assert.Equal(Key, PdfVoucherParser.AccessKey(PdfVoucherParser.ReadText(pdf)));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
