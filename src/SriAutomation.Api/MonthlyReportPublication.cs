using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

public static class MonthlyReportPublication
{
    public static PublishedMonthlyReport Generate(IStorageProvider storage, IStateRepository repository, string account, int year, int month, IEnumerable<Voucher> documents, Action<string, string>? beforeCommit = null)
    {
        if (storage is not DocumentStorage concrete) throw new InvalidOperationException("El almacenamiento no admite publicación verificada de reportes.");
        var gatePath = storage.Resolve(Path.Combine(".report-publication", Validation.Identifier(account), $"{year:D4}-{month:D2}.lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(gatePath)!);
        using var gate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var (_, bytes) = concrete.ReportSnapshot(account, year, month, documents);
        var hash = DownloadPipeline.Hash(bytes);
        var relative = Path.Combine(".objects", hash[..2], hash + ".xlsx");
        var target = storage.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target) && DownloadPipeline.Hash(File.ReadAllBytes(target)) == hash) { }
            if (DownloadPipeline.Hash(File.ReadAllBytes(target)) != hash) throw new IOException("El reporte publicado no supera la comprobación de integridad.");
            beforeCommit?.Invoke(relative, hash);
            var published = new PublishedMonthlyReport(account, year, month, relative, hash, DateTimeOffset.UtcNow);
            repository.Update(s => { s.PublishedReports.RemoveAll(x => x.Account == account && x.Year == year && x.Month == month); s.PublishedReports.Add(published); return true; });
            return published;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string? Find(State state, string account, int year, int month)
    {
        var direct = state.PublishedReports.Where(x => x.Account == account && x.Year == year && x.Month == month)
            .Select(x => (Path: x.Path, At: x.PublishedAt, Hash: x.Sha256));
        var execution = state.Executions.Where(e => e.Query.Account == account && (e.Status is "Completed" or "CompletedWithWarnings"))
            .SelectMany(e => e.Reports.Where(r => r.Year == year && r.Month == month).Select(r => (r.Path, At: e.Finished ?? DateTimeOffset.MinValue, Hash: Path.GetFileNameWithoutExtension(r.Path))));
        var selected = direct.Concat(execution).OrderByDescending(x => x.At).FirstOrDefault();
        if (selected.Path is null) return null;
        // Reject contradictory metadata instead of deriving trust from an arbitrary path.
        return string.Equals(Path.GetFileNameWithoutExtension(selected.Path), selected.Hash, StringComparison.OrdinalIgnoreCase) ? selected.Path : null;
    }
}
