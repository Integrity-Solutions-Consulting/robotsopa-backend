using SriAutomation.Application;
using System.Security.Cryptography;

namespace SriAutomation.Api;

public static class MonthlyReportEndpoint
{
    public static IResult Download(string account, int year, int month, IStorageProvider storage, IStateRepository repository)
    {
        var names = new[] { "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre" };
        _ = new DateOnly(year, month, 1); Validation.Identifier(account);
        var name = $"Resumen_{year:D4}_{month:D2}.xlsx";
        var path = storage.Resolve(Path.Combine(storage.AccountDirectory(account), year.ToString("D4"), $"{month:D2}-{names[month - 1]}", name));
        var committed = repository.Read(s => MonthlyReportPublication.Find(s, account, year, month));
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
        // Only content-addressed committed reports have a trusted stored hash.
        // Historical paths without one require a separate verified migration.
        if (committed is null || !committed.Replace('\\','/').StartsWith(".objects/", StringComparison.Ordinal))
            return Results.Conflict(new { detail = "El reporte histórico no tiene un hash verificable." });
        var expected = Path.GetFileNameWithoutExtension(committed);
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !string.Equals(path, storage.Resolve(committed), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Results.Conflict(new { detail = "El reporte vigente no está disponible o no tiene un hash verificable." });
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try {
            var actual = Convert.ToHexStringLower(SHA256.HashData(file));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { file.Dispose(); return Results.Conflict(new { detail = "El reporte no supera la comprobación de integridad." }); }
            file.Position = 0;
            return Results.File(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        } catch { file.Dispose(); throw; }
    }
}
