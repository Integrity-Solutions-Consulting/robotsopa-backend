using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class PendingPublicationRaceTests
{
    [Fact]
    public void GuardedPendingUploadCannotReplaceVersionCommittedAfterAvailabilityCheck()
    {
        const string key = "0109202601179000000000110010010000000011234567811";
        var issued = new DateOnly(2026, 10, 12);
        var stale = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "ANTERIOR"]);
        var newer = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "VIGENTE"]);
        var repo = new RacingRepository(new Voucher { Account = "a", Type = "Facturas", AccessKey = key, Issued = issued, PdfPath = "current.pdf", PdfSha256 = DownloadPipeline.Hash(newer) });
        var pipeline = new DownloadPipeline(repo, new FakeStorage(), new XmlDocumentParser(), onlyIfMissingOrSame: true);
        Assert.Throws<ArgumentException>(() => pipeline.Process(new("a", "Facturas", key, issued, null, stale)));
        Assert.Equal(DownloadPipeline.Hash(newer), Assert.Single(repo.State.Documents).PdfSha256);
    }
    private sealed class RacingRepository(Voucher concurrent) : IStateRepository
    {
        public State State { get; } = new() { Accounts = [new("a", "Fixture", "1790000000001")] };
        public T Read<T>(Func<State, T> read) => read(State);
        public T Update<T>(Func<State, T> update) { State.Documents.Add(concurrent); return update(State); }
    }
    private sealed class FakeStorage : IStorageProvider
    {
        public void CheckAccess() { }
        public string Save(Voucher document, string extension, byte[] content) => "staged." + extension;
        public string Resolve(string relative) => Path.Combine(Path.GetTempPath(), "not-used-" + Guid.NewGuid().ToString("N"));
        public string Report(string account, int year, int month, IEnumerable<Voucher> documents) => throw new NotSupportedException();
    }
}
