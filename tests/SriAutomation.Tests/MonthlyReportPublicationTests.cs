using SriAutomation.Api;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using ClosedXML.Excel;

namespace SriAutomation.Tests;

public sealed class MonthlyReportPublicationTests
{
    [Fact]
    public void PublishesVerifiedObjectBeforeRegisteringAndPreservesPriorReportOnFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "sopa-report-publication-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new DocumentStorage(root); var repository = new MemoryRepository();
            var report = MonthlyReportPublication.Generate(storage, repository, "synthetic", 2026, 10, []);
            Assert.Equal(report.Sha256, DownloadPipeline.Hash(File.ReadAllBytes(storage.Resolve(report.Path))));
            using (var book = new XLWorkbook(storage.Resolve(report.Path)))
            {
                Assert.NotNull(book.Worksheet("Resumen"));
                Assert.NotNull(book.Worksheet("Impuestos y retenciones"));
            }
            Assert.Equal(report.Path, MonthlyReportPublication.Find(repository.State, "synthetic", 2026, 10));
            Assert.Throws<IOException>(() => MonthlyReportPublication.Generate(storage, repository, "synthetic", 2026, 10, [], (_, _) => throw new IOException("injected failure")));
            Assert.Equal(report, Assert.Single(repository.State.PublishedReports));
            Assert.Null(MonthlyReportPublication.Find(repository.State, "other", 2026, 10));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MemoryRepository : IStateRepository
    {
        public State State { get; } = new();
        public T Read<T>(Func<State, T> read) => read(State);
        public T Update<T>(Func<State, T> update) => update(State);
    }
}
