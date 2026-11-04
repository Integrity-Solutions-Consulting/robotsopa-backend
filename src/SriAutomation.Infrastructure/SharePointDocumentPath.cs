using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>Builds deterministic SharePoint voucher paths without relying on local filesystem paths.</summary>
public static class SharePointDocumentPath
{
    public static string Build(string folder, string taxId, Voucher document, string extension)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (taxId is null || taxId.Length != 13 || taxId.Any(c => !char.IsAsciiDigit(c)))
            throw new ArgumentException("Se requiere un RUC de 13 dígitos.", nameof(taxId));
        if (!Validation.Types.Contains(document.Type) || extension is not ("xml" or "pdf"))
            throw new ArgumentException("Tipo o extensión inválidos.");
        var prefix = (folder ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (prefix.Any(part => part is "." or ".." || part.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '#', '%']) >= 0 || part.Any(char.IsControl) || part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("Carpeta de SharePoint inválida.", nameof(folder));
        var name = Validation.Identifier(document.AccessKey) + "." + extension;
        return string.Join('/', prefix.Concat([taxId, document.Issued.Year.ToString("D4"), document.Issued.Month.ToString("D2"), document.Type, name]));
    }
}
