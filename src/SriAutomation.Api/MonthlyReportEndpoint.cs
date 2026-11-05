using SriAutomation.Application;

namespace SriAutomation.Api;

public static class MonthlyReportEndpoint
{
    public static IResult Download(string account, int year, int month, IStorageProvider storage, IStateRepository repository)
    {
        var names = new[] { "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre" };
        _ = new DateOnly(year, month, 1); Validation.Identifier(account);
        var name = $"Resumen_{year:D4}_{month:D2}.xlsx";
        var path = storage.Resolve(Path.Combine(storage.AccountDirectory(account), year.ToString("D4"), $"{month:D2}-{names[month - 1]}", name));
        var committed = repository.Read(s => s.Executions.Where(e => e.Query.Account == account && (e.Status is "Completed" or "CompletedWithWarnings"))
            .OrderByDescending(e => e.Finished).SelectMany(e => e.Reports).FirstOrDefault(report => report.Year == year && report.Month == month)?.Path);
        if (committed is not null && File.Exists(storage.Resolve(committed))) path = storage.Resolve(committed);
        if (!File.Exists(path))
        {
            var annual = storage.Resolve(Path.Combine(year.ToString("D4"), $"Resumen_{year:D4}_{account}.xlsx"));
            if (File.Exists(annual)) path = annual;
            var candidates = repository.Read(s => s.Documents.Where(d => d.Account == account && d.Issued.Year == year && d.Issued.Month == month)
                .Select(d => d.XmlPath ?? d.PdfPath).Where(p => p is not null).Select(p => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(p!))!, name)).Distinct().ToList());
            var previous = candidates.Select(storage.Resolve).FirstOrDefault(File.Exists);
            if (!File.Exists(path) && previous is not null) path = previous;
        }
        if (!File.Exists(path)) throw new KeyNotFoundException("Genere primero el reporte.");
        return Results.File(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }
}
