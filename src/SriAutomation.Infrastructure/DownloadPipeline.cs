using System.Security.Cryptography;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class DownloadPipeline(IStateRepository repository, IStorageProvider storage, IDocumentParser parser, OwnedContentStore? ownedContent = null) : IDownloadPipeline
{
    public bool IsAvailable(Voucher document, string extension)
    {
        var relative = extension == "xml" ? document.XmlPath : document.PdfPath;
        var expected = extension == "xml" ? document.XmlSha256 : document.PdfSha256;
        if (relative is null || expected is null) return false;
        var path = storage.Resolve(relative);
        return File.Exists(path) && new FileInfo(path).Length <= 20_000_000 && Hash(File.ReadAllBytes(path)) == expected;
    }
    public Voucher Process(DownloadedVoucher download)
    {
        if (download.Xml is { Length: 0 or > 20_000_000 } || download.Pdf is { Length: 0 or > 20_000_000 })
            throw new DocumentSizeException();
        if (repository is OwnedStateRepository && ownedContent is null) throw new InvalidOperationException("La ejecución distribuida requiere publicación protegida.");
        if (ownedContent is not null && !ownedContent.Uses(repository)) throw new InvalidOperationException("La publicación requiere el repositorio propietario asociado.");
        var account = repository.Read(s => s.Accounts.SingleOrDefault(a => a.Id == download.Account)) ?? throw new ArgumentException("Cuenta no registrada.");
        if (download.Xml is null && download.Pdf is null) throw new ArgumentException("No hay archivos disponibles para este comprobante.");
        var document = download.Xml is not null ? parser.ParseXml(download.Account, download.Xml)
            : new PdfVoucherParser().Parse(download.Account, download.Type, download.Key, download.Issued, download.Pdf!);
        if (document.AccessKey != download.Key || document.Type != download.Type || document.Issued != download.Issued) throw new ArgumentException("El documento no coincide con la clave, tipo o emisión de la fila consultada.");
        if (document.IssuerTaxId is not null || download.Xml is not null) Validation.DocumentAccount(account, document);
        if (download.Pdf is not null)
        {
            var pdfText = PdfVoucherParser.ReadText(download.Pdf);
            var pdfKey = PdfVoucherParser.AccessKey(pdfText);
            if (pdfKey is not null && pdfKey != document.AccessKey) throw new ArgumentException("PDF y XML pertenecen a comprobantes diferentes.");
            if (new PdfInspector().Check(download.Pdf, document.AccessKey) is { } warning) { document.Status = "Revisar"; document.Warning = string.Join(" ", document.Warning, warning); }
        }
        document.Source = "Sri";
        if (download.Xml is not null) { document.XmlPath = Save("xml", download.Xml); document.XmlSha256 = Hash(download.Xml); }
        if (download.Pdf is not null) { document.PdfPath = Save("pdf", download.Pdf); document.PdfSha256 = Hash(download.Pdf); }
        return repository.Update(s =>
        {
            var existing = s.Documents.SingleOrDefault(d => d.Account == document.Account && d.Type == document.Type && d.AccessKey == document.AccessKey);
            if (existing is null) { s.Documents.Add(document); return document; }
            if (existing.XmlSha256 is not null && document.XmlSha256 is not null && existing.XmlSha256 != document.XmlSha256 || existing.PdfSha256 is not null && document.PdfSha256 is not null && existing.PdfSha256 != document.PdfSha256) throw new ArgumentException("Colisión de integridad en el manifiesto.");
            document.Id = existing.Id;
            document.XmlPath ??= existing.XmlPath; document.XmlSha256 ??= existing.XmlSha256;
            document.PdfPath ??= existing.PdfPath; document.PdfSha256 ??= existing.PdfSha256;
            if (download.Xml is null && existing.XmlPath is not null)
            { existing.PdfPath = document.PdfPath; existing.PdfSha256 = document.PdfSha256; existing.Status = "Revisar"; existing.Warning = "PDF incorporado a XML existente; revisar conciliación."; return existing; }
            s.Documents[s.Documents.IndexOf(existing)] = document; return document;
        });
        string Save(string extension, byte[] bytes) => ownedContent is null
            ? storage.Save(document, extension, bytes)
            : ownedContent.Publish(bytes, extension, (_, path, _) => path);
    }
    private static void QueueUploads(State state, Voucher document, Account account, DownloadedVoucher download)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SRI_SHAREPOINT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)) return;
        if (state.Executions.LastOrDefault(e => e.Query.Account == document.Account && e.Status == "Running")?.Query.StorageMode == "LOCAL_WINDOWS") return;
        var taxId = account.TaxId ?? (account.Id.Length == 13 ? account.Id : "");
        var folder = Environment.GetEnvironmentVariable("SRI_SHAREPOINT_FOLDER") ?? "";
        foreach (var extension in new[] { "xml", "pdf" })
        {
            if (extension == "xml" ? download.Xml is null : download.Pdf is null) continue;
            var path = extension == "xml" ? document.XmlPath! : document.PdfPath!;
            var hash = extension == "xml" ? document.XmlSha256! : document.PdfSha256!;
            var remote = SharePointDocumentPath.Build(folder, taxId, document, extension);
            var existing = state.SharePointUploads.FirstOrDefault(u => u.DocumentId == document.Id && u.Extension == extension);
            if (existing is not null)
            {
                if (existing.Sha256 != hash || existing.RemotePath != remote) throw new InvalidDataException("Conflicto en cola SharePoint.");
                continue;
            }
            var user = state.Executions.LastOrDefault(e => e.Query.Account == document.Account && e.Status == "Running")?.RequestedBy ?? "Sistema (Worker)";
            state.SharePointUploads.Add(new SharePointUpload { DocumentId = document.Id, Account = document.Account, TaxId = taxId, User = user, Extension = extension, LocalPath = path, RemotePath = remote, Sha256 = hash });
            AuditTrail.Add(state, user, "Publicación SharePoint", document.Account, $"Pendiente: {remote}", account.IsTest);
        }
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
