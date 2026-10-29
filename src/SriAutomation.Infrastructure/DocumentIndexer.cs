using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class DocumentIndexer(IStateRepository repository, IStorageProvider storage, IDocumentParser parser)
{
    public object Rebuild(string account)
    {
        Validation.Identifier(account);
        var registeredAccount = repository.Read(s => s.Accounts.SingleOrDefault(a => a.Id == account)) ?? throw new ArgumentException("Cuenta inexistente.");
        var basePath = Path.GetDirectoryName(storage.Resolve(".index"))!;
        if (!Directory.Exists(basePath)) return new { indexed = 0, errors = 0 };
        var documents = new List<Voucher>(); var errors = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(basePath, "*.xml", options))
        {
            try
            {
                if (new FileInfo(path).Length > 20_000_000) { errors++; continue; }
                var relativePath = Path.GetRelativePath(basePath, path);
                var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var accountParts = storage.AccountDirectory(account).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var isCurrent = parts.Length >= 4 + accountParts.Length && parts.Skip(2).Take(accountParts.Length).SequenceEqual(accountParts);
                var isLegacy = parts.Length >= accountParts.Length + 4 && parts.Take(accountParts.Length).SequenceEqual(accountParts);
                if (!isCurrent && !isLegacy) continue;
                var doc = parser.ParseXml(account, File.ReadAllBytes(path));
                Validation.DocumentAccount(registeredAccount, doc);
                doc.XmlPath = storage.Save(doc, "xml", File.ReadAllBytes(path));
                doc.XmlSha256 = DownloadPipeline.Hash(File.ReadAllBytes(path));
                var pdf = Path.ChangeExtension(doc.XmlPath, ".pdf");
                if (File.Exists(storage.Resolve(pdf)))
                {
                    doc.PdfPath = pdf;
                    if (new FileInfo(storage.Resolve(pdf)).Length <= 20_000_000) doc.PdfSha256 = DownloadPipeline.Hash(File.ReadAllBytes(storage.Resolve(pdf)));
                    string? warning;
                    try { warning = new FileInfo(storage.Resolve(pdf)).Length > 20_000_000 ? "PDF demasiado grande para verificar." : new PdfInspector().Check(File.ReadAllBytes(storage.Resolve(pdf)), doc.AccessKey); }
                    catch { warning = "PDF no válido; requiere revisión manual."; }
                    if (warning is not null) { doc.Status = "Revisar"; doc.Warning = string.Join(" ", doc.Warning, warning); }
                }
                documents.Add(doc);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or System.Xml.XmlException) { errors++; }
        }
        repository.Update(s =>
        {
            foreach (var doc in documents)
            {
                var index = s.Documents.FindIndex(d => d.Account == account && d.Type == doc.Type && d.AccessKey == doc.AccessKey);
                if (index < 0) s.Documents.Add(doc);
                else { doc.Id = s.Documents[index].Id; doc.Source = s.Documents[index].Source; s.Documents[index] = doc; }
            }
            return true;
        });
        return new { indexed = documents.Count, errors };
    }
}
