using SriAutomation.Infrastructure;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Api;

// PostgreSQL stores state; archive bytes are temporary and instance-local.
public sealed class DocumentZipService : IDisposable
{
    private const int MaxFiles = 2000;
    private const long MaxBytes = 512L * 1024 * 1024;
    private readonly IStateRepository repository;
    private readonly IStorageProvider storage;
    private readonly IZipJobStore store;
    private readonly ILogger<DocumentZipService> logger;
    private readonly SemaphoreSlim pump = new(1, 1);
    private readonly Guid instanceId = Guid.NewGuid();
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "sopa-document-zips-v2");
    private string instanceRoot => Path.Combine(tempRoot, instanceId.ToString("N"));
    private readonly Timer timer;
    public sealed record Request(int? Year, int? Month, string? Type, string? Formats = null);
    private sealed record Entry(string Relative, string? Hash, string Name, string Label);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private (List<Entry> files, List<string> issues, List<string> warnings, string taxId) Manifest(string account, Request scope)
    {
        if (scope.Formats is not (null or "auto" or "pdf" or "xml" or "both")) throw new ArgumentException("Selección de formatos inválida.");
        if (scope.Year is < 1 or > 9999 || scope.Month is < 1 or > 12 || scope.Month.HasValue && !scope.Year.HasValue || scope.Type is not null && (!scope.Month.HasValue || !Validation.Types.Contains(scope.Type))) throw new ArgumentException("Selección de carpeta inválida.");
        return repository.Read(s => {
            var a = s.Accounts.Single(x => x.Id == account); var taxId = a.TaxId ?? a.Id;
            if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new ArgumentException("RUC inválido.");
            bool InScope(int y, int m) => (!scope.Year.HasValue || y == scope.Year) && (!scope.Month.HasValue || m == scope.Month);
            var files = new List<Entry>(); var issues = new List<string>(); var warnings = new List<string>();
            var sourcesByKey = s.Executions.Where(e => e.Query.Account == account && e.Query.Mode == "Sri")
                .SelectMany(e => e.AttemptedKeys.Select(key => (key, execution: e))).ToLookup(x => x.key, x => x.execution);
            foreach (var d in s.Documents.Where(d => d.Account == account && InScope(d.Issued.Year, d.Issued.Month) && (scope.Type == null || d.Type == scope.Type))) {
                if (!Validation.Types.Contains(d.Type) || d.AccessKey.Length != 49 || !d.AccessKey.All(char.IsAsciiDigit)) { issues.Add("Comprobante con identidad inválida: " + d.Id); continue; }
                var sources = sourcesByKey[d.AccessKey].Where(e => e.Query.From <= d.Issued && e.Query.To >= d.Issued && e.Query.Types.Contains(d.Type)).ToList();
                if ((scope.Formats is null or "auto") && sources.Count == 0) warnings.Add($"{d.AccessKey}: formatos originales desconocidos; se exportan los archivos registrados.");
                foreach (var (kind, path, hash) in new[] { ("pdf", d.PdfPath, d.PdfSha256), ("xml", d.XmlPath, d.XmlSha256) }) {
                    var selected = scope.Formats switch {
                        "pdf" => kind == "pdf", "xml" => kind == "xml", "both" => true,
                        _ => path is not null || sources.Any(e => kind == "pdf" ? e.Query.Pdf : e.Query.Xml)
                    };
                    if (!selected) continue;
                    if (path is null) { issues.Add($"{d.AccessKey}.{kind}: no registrado"); continue; }
                    var name = $"{taxId}/{d.Issued.Year:D4}/{d.Issued.Month:D2}/{d.Type}/{d.AccessKey}.{kind}";
                    files.Add(new(path, hash, name, $"{d.AccessKey}.{kind}"));
                }
            }
            if (scope.Type is null) {
                var periods = s.Executions.Where(e => e.Query.Account == account && (e.Status is "Completed" or "CompletedWithWarnings"))
                    .SelectMany(e => e.Reports).Select(r => (r.Year,r.Month))
                    .Concat(s.PublishedReports.Where(r => r.Account == account).Select(r => (r.Year,r.Month)))
                    .Where(r => InScope(r.Year,r.Month)).Distinct();
                var reports = periods.Select(p => new ExecutionReport(p.Year,p.Month,MonthlyReportPublication.Find(s, account, p.Year,p.Month) ?? ""));
                foreach (var report in reports) {
                    var filename = Path.GetFileNameWithoutExtension(report.Path);
                    var hash = filename.Length == 64 && filename.All(Uri.IsHexDigit) && report.Path.Replace('\\','/').StartsWith(".objects/",StringComparison.Ordinal) ? filename.ToLowerInvariant() : null;
                    files.Add(new(report.Path, hash, $"{taxId}/{report.Year:D4}/{report.Month:D2}/Reportes/Resumen_{report.Year:D4}_{report.Month:D2}.xlsx", $"Reporte {report.Year:D4}-{report.Month:D2}"));
                }
            }
            if (files.Count > MaxFiles) throw new ArgumentException("La selección supera el máximo de 2000 archivos.");
            return (files, issues, warnings, taxId);
        });
    }
    public DocumentZipService(IStateRepository repository, IStorageProvider storage, IZipJobStore store, ILogger<DocumentZipService>? logger = null)
    {
        this.repository = repository; this.storage = storage; this.store = store;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentZipService>.Instance;
        Directory.CreateDirectory(instanceRoot);
        timer = new Timer(_ => Schedule(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }
    public object Start(string user, string account, Request scope)
    {
        var (files, issues, warnings, _) = Manifest(account, scope);
        var signature = Hash(JsonSerializer.Serialize(new { account, scope, files, issues, warnings }));
        var existing = store.FindReusable(user, account, signature);
        if (existing is not null && (existing.Status != "Ready" || ArchiveValid(existing))) return View(existing);
        var id = Guid.NewGuid();
        store.Create(id, user, account, JsonSerializer.Serialize(scope), signature, files.Count, DateTimeOffset.UtcNow.AddMinutes(30));
        Schedule();
        return View(store.Find(id, user, account) ?? throw new InvalidOperationException("ZIP no registrado."));
    }
    private void Schedule()
    {
        if (!pump.Wait(0)) return;
        _ = Task.Run(() => {
            try {
                store.Expire(); Cleanup();
                foreach (var id in store.Recoverable(20)) {
                    var lease = store.Claim(id, instanceId, TimeSpan.FromMinutes(15));
                    if (lease is not null) Prepare(lease);
                }
            } catch (Exception ex) { logger.LogWarning("ZIP recovery failed: {Type}", ex.GetType().Name); }
            finally { pump.Release(); }
        });
    }
    private void Prepare(ZipJobLease lease)
    {
        var name = lease.JobId.ToString("N") + "-" + lease.LeaseId.ToString("N") + ".zip";
        var partial = Path.Combine(instanceRoot, name + ".part"); var final = Path.Combine(instanceRoot, name);
        var publicationAttempted = false;
        try {
            var job = store.FindForProcessing(lease.JobId) ?? throw new IOException("Trabajo no disponible.");
            if (!store.Authorized(job.User, job.Account)) throw new IOException("Acceso revocado.");
            var scope = JsonSerializer.Deserialize<Request>(job.Selection) ?? throw new IOException("Selección inválida.");
            var (files, issues, warnings, _) = Manifest(job.Account, scope);
            if (Hash(JsonSerializer.Serialize(new { account = job.Account, scope, files, issues, warnings })) != job.Signature) throw new IOException("Selección modificada.");
            if (files.Count == 0) issues.Add("La carpeta no contiene archivos registrados.");
            if (files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) issues.Add("Hay nombres duplicados en la selección.");
            long total = 0; var deadline = DateTime.UtcNow.AddMinutes(10); var done = 0; var omitted = 0;
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create)) {
                foreach (var entry in files) {
                    if (DateTime.UtcNow > deadline || !store.Renew(lease, TimeSpan.FromMinutes(15))) throw new IOException("Lease vencido o tiempo excedido.");
                    var included = false;
                    try {
                        if (entry.Hash is null || entry.Hash.Length != 64 || !entry.Hash.All(Uri.IsHexDigit)) { issues.Add(entry.Label + ": sin hash verificable"); continue; }
                        using var input = new FileStream(storage.Resolve(entry.Relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                        total = checked(total + input.Length); if (total > MaxBytes) throw new IOException("Límite de tamaño excedido.");
                        if (!Convert.ToHexStringLower(SHA256.HashData(input)).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase)) { issues.Add(entry.Label + ": integridad incorrecta"); continue; }
                        input.Position = 0; using var output = archive.CreateEntry(entry.Name, CompressionLevel.Fastest).Open(); input.CopyTo(output); included = true;
                    } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or ArgumentException or UnauthorizedAccessException) { issues.Add(entry.Label + ": no disponible"); }
                    finally { done++; if (!included) omitted++; if (!store.Progress(lease, done, omitted)) throw new IOException("Lease vencido."); }
                }
            }
            if (issues.Count > 0) {
                File.Delete(partial);
                store.Finish(lease, "Incomplete", null, null, null, JsonSerializer.Serialize(issues), JsonSerializer.Serialize(warnings));
                return;
            }
            if (!store.Authorized(job.User, job.Account) || !store.Renew(lease, TimeSpan.FromMinutes(15))) throw new IOException("Acceso o lease vencido.");
            File.Move(partial, final, false);
            using var completed = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(completed)); var size = completed.Length;
            publicationAttempted = true;
            if (!store.Finish(lease, "Ready", name, hash, size, "[]", JsonSerializer.Serialize(warnings))) { completed.Dispose(); File.Delete(final); }
        } catch (Exception ex) {
            logger.LogWarning("ZIP preparation failed: {Type}", ex.GetType().Name);
            try { if (File.Exists(partial)) File.Delete(partial); if (!publicationAttempted && File.Exists(final)) File.Delete(final); } catch (IOException) { }
            try { store.Finish(lease, "Failed", null, null, null, JsonSerializer.Serialize(new[] { "Preparación interrumpida o no autorizada; solicite nuevamente." }), "[]"); } catch (Exception) { }
        }
    }
    private string? LocalArchive(StoredZipJob job)
    {
        if (job.InstanceId is null || job.ArchiveName is null || job.ArchiveSha256 is null || job.ArchiveSize is null) return null;
        if (Path.GetFileName(job.ArchiveName) != job.ArchiveName || !job.ArchiveName.StartsWith(job.Id.ToString("N") + "-", StringComparison.Ordinal)) return null;
        var path = Path.Combine(tempRoot, job.InstanceId.Value.ToString("N"), job.ArchiveName);
        return File.Exists(path) && new FileInfo(path).Length == job.ArchiveSize ? path : null;
    }
    private object View(StoredZipJob job)
    {
        var issues = JsonSerializer.Deserialize<string[]>(job.Issues) ?? [];
        var status = job.Status switch { "Pending" or "Preparing" => "Preparando", "Ready" => "Listo", "Incomplete" => "Incompleto", _ => "Error" };
        var message = job.Status switch {
            "Pending" => "En espera de preparación.",
            "Preparing" when job.LeaseExpiresAt.HasValue && job.LeaseExpiresAt <= DateTimeOffset.UtcNow => "Preparación interrumpida; esperando recuperación segura.",
            "Preparing" when job.InstanceId != instanceId => "Preparación asignada a otra instancia; si se interrumpe se recuperará al vencer su lease.",
            "Preparing" => "Preparando y verificando archivos.",
            _ => ""
        };
        if (job.Status == "Ready" && !ArchiveValid(job)) { status = "Error"; issues = ["ZIP ausente o no verificable en esta instancia; solicite una nueva preparación."]; }
        return new { id = job.Id.ToString("N"), status, message, done = job.Processed, total = job.Expected, issues, warnings = JsonSerializer.Deserialize<string[]>(job.Warnings) ?? [] };
    }
    private bool ArchiveValid(StoredZipJob job)
    {
        try {
            var path = LocalArchive(job); if (path is null) return false;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(SHA256.HashData(input)).Equals(job.ArchiveSha256, StringComparison.OrdinalIgnoreCase);
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
    private StoredZipJob Get(string user, string account, string id) => Guid.TryParse(id, out var parsed) ? store.Find(parsed, user, account) ?? throw new KeyNotFoundException() : throw new KeyNotFoundException();
    public object Status(string user, string account, string id) => View(Get(user, account, id));
    public IResult Download(string user, string account, string id)
    {
        var job = Get(user, account, id); if (job.Status != "Ready") return Results.Conflict();
        var path = LocalArchive(job); if (path is null) return Results.Conflict(new { detail = "ZIP no disponible en esta instancia; solicite nuevamente." });
        var scope = JsonSerializer.Deserialize<Request>(job.Selection) ?? throw new InvalidDataException("Selección inválida.");
        var (files, issues, warnings, taxId) = Manifest(account, scope);
        if (Hash(JsonSerializer.Serialize(new { account, scope, files, issues, warnings })) != job.Signature) return Results.Conflict(new { detail = "La selección cambió; prepare nuevamente el ZIP." });
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexStringLower(SHA256.HashData(input)).Equals(job.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) { input.Dispose(); return Results.Conflict(new { detail = "ZIP no supera la comprobación de integridad." }); }
        input.Position = 0;
        var name = scope.Type is not null ? $"{scope.Type}_{scope.Year:D4}_{scope.Month:D2}.zip" : scope.Month.HasValue ? $"Comprobantes_{scope.Year:D4}_{scope.Month:D2}.zip" : scope.Year.HasValue ? $"Comprobantes_{scope.Year:D4}.zip" : $"Comprobantes_{taxId}.zip";
        return Results.File(input, "application/zip", name);
    }
    private void Cleanup()
    {
        // Only files with our generated names, older than all allowed job leases and expiration.
        if (!Directory.Exists(tempRoot) || new DirectoryInfo(tempRoot).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (var dir in Directory.EnumerateDirectories(tempRoot)) {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _) || new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var path in Directory.EnumerateFiles(dir)) {
                var name = Path.GetFileName(path); var stem = name.EndsWith(".zip.part", StringComparison.Ordinal) ? name[..^9] : name.EndsWith(".zip", StringComparison.Ordinal) ? name[..^4] : "";
                var parts = stem.Split('-');
                if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) || !Guid.TryParseExact(parts[1], "N", out _) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-1)) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    public void Dispose() => timer.Dispose();
}
