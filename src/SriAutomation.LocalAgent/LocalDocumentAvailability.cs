using System.Security.Cryptography;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.Application;
namespace SriAutomation.LocalAgent;
internal static class LocalDocumentAvailability
{
    internal static bool IsAvailable(string root, string taxId, Voucher document, string extension)
    {
        if (extension is not ("xml" or "pdf")) throw new ArgumentException("Formato inválido.");
        var expected = extension == "xml" ? document.XmlSha256 : document.PdfSha256;
        if (expected is null || !Validation.Types.Contains(document.Type)) return false;
        var relative = Path.Combine(Validation.Identifier(taxId), document.Issued.Year.ToString("D4"), document.Issued.Month.ToString("D2"), document.Type, Validation.Identifier(document.AccessKey) + "." + extension);
        var path = new DocumentStorage(root).Resolve(relative);
        if (!File.Exists(path) || new FileInfo(path).Length > 20_000_000) return false;
        using var file = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(file)), expected, StringComparison.OrdinalIgnoreCase);
    }
}
