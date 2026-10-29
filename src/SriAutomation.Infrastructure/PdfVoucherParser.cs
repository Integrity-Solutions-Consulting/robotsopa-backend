using System.Globalization;
using System.Text.RegularExpressions;
using SriAutomation.Domain;
using UglyToad.PdfPig;

namespace SriAutomation.Infrastructure;

public sealed class PdfVoucherParser
{
    public static string ReadText(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages == 0) throw new ArgumentException("PDF sin páginas.");
            // Reconstruir líneas visuales en lugar de concatenar columnas de todo el documento.
            return string.Join("\n", pdf.GetPages().SelectMany(p => p.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3) * 3)
                .OrderByDescending(g => g.Key).Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))));
        }
        catch (UglyToad.PdfPig.Core.PdfDocumentFormatException) { throw new ArgumentException("La descarga PDF no contiene un PDF válido."); }
    }
    private static string? Field(string text, string label, string capture = @"[^\r\n]+")
    {
        var separator = capture == @"[^\r\n]+" ? @"[\t ]*:?[\t ]*" : @"\s*:?\s*";
        var matches = Regex.Matches(text, @"(?im)^[\t ]*(?:" + label + @")" + separator + "(" + capture + @")[\t ]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return matches.Count == 1 ? matches[0].Groups[1].Value.Trim() : null;
    }
    public static string? AccessKey(string text) => Field(text, @"CLAVE\s+DE\s+ACCESO", @"(?:\d[ -]*){49}") is { } raw ? new string(raw.Where(char.IsAsciiDigit).ToArray()) : null;
    public Voucher Parse(string account, string type, string key, DateOnly issued, byte[] pdf)
    {
        var text = ReadText(pdf);
        var pdfKey = AccessKey(text);
        if (pdfKey is not null && pdfKey != key) throw new ArgumentException("El PDF pertenece a una clave diferente.");
        var date = Field(text, @"FECHA\s+DE\s+EMISI[ÓO]N", @"\d{2}/\d{2}/\d{4}");
        if (date is not null && DateOnly.TryParseExact(date, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pdfDate) && pdfDate != issued) throw new ArgumentException("La fecha de emisión PDF no coincide con la fila.");
        decimal? Money(string label)
        {
            var raw = Field(text, label, @"-?\d+(?:\.\d{3})*,\d{2}|-?\d+\.\d{2}");
            if (raw is null) return null;
            var normalized = raw.Contains(',') ? raw.Replace(".", "").Replace(',', '.') : raw;
            return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;
        }
        // Estos rótulos se refieren exclusivamente al receptor; nunca usar razón social genérica.
        var recipientLabel = type == "Retenciones" ? @"RAZ[ÓO]N\s+SOCIAL\s+(?:DEL\s+)?SUJETO\s+RETENIDO" : @"RAZ[ÓO]N\s+SOCIAL\s*/\s*NOMBRES\s+Y\s+APELLIDOS|RECEPTOR";
        return new Voucher
        {
            Account = account,
            Type = type,
            AccessKey = key,
            Issued = issued,
            Number = Field(text, @"(?:FACTURA|COMPROBANTE|NOTA\s+DE\s+CR[ÉE]DITO)\s*(?:NO\.?|NRO\.?)?", @"\d{3}-\d{3}-\d{9}"),
            Authorization = Field(text, @"N[ÚU]MERO\s+DE\s+AUTORIZACI[ÓO]N", @"\d+"),
            AuthorizedAt = Field(text, @"FECHA\s+Y\s+HORA\s+DE\s+AUTORIZACI[ÓO]N"),
            Recipient = Field(text, recipientLabel),
            Identification = Field(text, @"IDENTIFICACI[ÓO]N(?:\s+DEL\s+RECEPTOR)?", @"[A-Za-z0-9-]+"),
            IssuerTaxId = Field(text, @"R\.?U\.?C\.?", @"\d{13}"),
            Subtotal = type == "Retenciones" ? null : Money(@"SUBTOTAL\s+SIN\s+IMPUESTOS"),
            Taxes = type == "Retenciones" ? null : Money(@"TOTAL\s+IMPUESTOS"),
            Total = Money(type == "Retenciones" ? @"TOTAL\s+RETENIDO" : type == "Notas-Credito" ? @"VALOR\s+(?:DE\s+)?MODIFICACI[ÓO]N" : @"IMPORTE\s+TOTAL|VALOR\s+TOTAL|TOTAL\s+PAGADO"),
            Status = "Revisar",
            Warning = "Datos extraídos del PDF por rótulos; revise los campos incompletos." + (pdfKey is null ? " Clave no corroborada en el texto del PDF." : "")
        };
    }
}
