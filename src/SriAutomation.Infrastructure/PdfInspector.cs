using UglyToad.PdfPig;
namespace SriAutomation.Infrastructure;

public sealed class PdfInspector
{
    public string? Check(byte[] bytes, string accessKey)
    {
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Contains(accessKey, StringComparison.Ordinal) ? null : "La clave de la consulta no pudo corroborarse en el PDF. Revisión manual requerida.";
    }
}
