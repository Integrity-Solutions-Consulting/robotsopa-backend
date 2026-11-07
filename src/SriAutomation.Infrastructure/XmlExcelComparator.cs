using ClosedXML.Excel;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed record ComparisonDifference(string Field, string Kind, string Expected, string Actual);
public sealed record ComparisonCoverage(IReadOnlyList<ComparisonDifference> Differences, IReadOnlyList<string> Verified, IReadOnlyList<string> NotVerified);

/// <summary>Local comparison; sensitive strings are masked in the returned differences.</summary>
public static class XmlExcelComparator
{
    public static ComparisonCoverage CompareDetailed(byte[] xml, string excelPath) => new(
        Compare(xml, excelPath),
        ["Fecha de emisión", "Fecha y hora de autorización cuando son interpretables", "Número de autorización", "Identificación del receptor", "Emisor y receptor en resúmenes no retención", "Subtotal y total", "Campos XML conservados", "Códigos y descripciones seleccionados", "Valores numéricos seleccionados de detalle e impuestos", "Bases, porcentajes, valores y documentos de retención"],
        ["Nombre del emisor y del receptor en todas las hojas", "Todas las columnas de documentos relacionados y destinatarios", "Fórmulas y totales agregados del libro", "Exactitud tributaria o validez oficial del XML"]);
    public static IReadOnlyList<ComparisonDifference> Compare(byte[] xml, string excelPath)
    {
        var voucher = new XmlDocumentParser().ParseXml("comparacion-local", xml);
        using var workbook = new XLWorkbook(excelPath);
        var differences = new List<ComparisonDifference>();
        static string Mask(string? value) => value is null ? "[ausente]" : "[presente]";
        void CompareText(string field, string? expected, string? actual)
        {
            if (string.Equals(expected ?? "", actual ?? "", StringComparison.Ordinal)) return;
            differences.Add(new(field, string.IsNullOrEmpty(actual) ? "ausente" : "incorrecto", Mask(expected), Mask(actual)));
        }
        void CompareNumber(string field, decimal? expected, IXLCell cell)
        {
            if (!expected.HasValue && cell.IsEmpty()) return;
            if (!expected.HasValue || cell.IsEmpty() || !cell.TryGetValue<decimal>(out var value) || value != expected.Value)
                differences.Add(new(field, cell.IsEmpty() ? "ausente" : "incorrecto", expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "[ausente]", cell.IsEmpty() ? "[ausente]" : cell.GetFormattedString()));
        }
        var main = workbook.Worksheets.FirstOrDefault(w => w.Name == "Documentos");
        var matches = main?.RowsUsed().Skip(1).Where(r => r.Cell(1).GetString() == voucher.Type && r.Cell(2).GetString() == (voucher.Number ?? "")).ToList() ?? [];
        if (matches.Count != 1) differences.Add(new("Documento", matches.Count == 0 ? "ausente" : "duplicado", "1", matches.Count.ToString()));
        else
        {
            var row = matches[0];
            CompareText("Autorización", voucher.Authorization, row.Cell(3).GetString());
            CompareText("Identificación", voucher.Identification, row.Cell(6).GetString());
            CompareNumber("Subtotal", voucher.Subtotal, row.Cell(7));
            CompareNumber("Total", voucher.Total, row.Cell(8));
            var date = row.Cell(9);
            if (!date.TryGetValue<DateTime>(out var actualDate) || DateOnly.FromDateTime(actualDate) != voucher.Issued)
                differences.Add(new("Fecha", date.IsEmpty() ? "ausente" : "incorrecto", voucher.Issued.ToString("dd/MM/yyyy"), date.IsEmpty() ? "[ausente]" : date.GetFormattedString()));
            var auth = voucher.AuthorizedAt?.Trim();
            if (auth?.Length >= 10 && DateOnly.TryParseExact(auth[..10], ["yyyy-MM-dd", "dd/MM/yyyy"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expectedAuthDate))
            {
                var authorizationDate = row.Cell(4);
                if (!authorizationDate.TryGetValue<DateTime>(out var actualAuthDate) || DateOnly.FromDateTime(actualAuthDate) != expectedAuthDate)
                    differences.Add(new("Fecha de autorización", authorizationDate.IsEmpty() ? "ausente" : "incorrecto", expectedAuthDate.ToString("dd/MM/yyyy"), authorizationDate.IsEmpty() ? "[ausente]" : authorizationDate.GetFormattedString()));
            }
            if (auth?.Length >= 19 && TimeOnly.TryParseExact(auth.Substring(11, 8), "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expectedTime))
            {
                var hour = row.Cell(10);
                if (!hour.TryGetValue<TimeSpan>(out var actualTime) || actualTime != expectedTime.ToTimeSpan())
                    differences.Add(new("Hora", hour.IsEmpty() ? "ausente" : "incorrecto", expectedTime.ToString("HH:mm:ss"), hour.IsEmpty() ? "[ausente]" : hour.GetFormattedString()));
            }
        }
        if (voucher.Type != "Retenciones")
        {
            var (summaryName, section, extras) = voucher.Type switch
            {
                "Facturas" => ("Facturas", "infoFactura", new[] { "moneda", "totalDescuento", "direccionComprador" }),
                "Liquidaciones" => ("Liquidaciones", "infoLiquidacionCompra", new[] { "moneda", "totalDescuento", "direccionProveedor" }),
                "Notas-Credito" => ("Notas crédito", "infoNotaCredito", new[] { "codDocModificado", "numDocModificado", "fechaEmisionDocSustento", "motivo" }),
                "Notas-Debito" => ("Notas débito", "infoNotaDebito", new[] { "codDocModificado", "numDocModificado", "fechaEmisionDocSustento" }),
                "Guias-Remision" => ("Guías remisión", "infoGuiaRemision", new[] { "razonSocialTransportista", "rucTransportista", "dirPartida", "fechaIniTransporte", "fechaFinTransporte", "placa" }),
                _ => ("Otros comprobantes", "", Array.Empty<string>())
            };
            var summary = workbook.Worksheets.FirstOrDefault(w => w.Name == summaryName);
            var summaryRows = summary?.RowsUsed().Skip(1).Where(r => r.Cell(3).GetString() == voucher.AccessKey).ToList() ?? [];
            if (summaryRows.Count != 1) differences.Add(new(summaryName, summaryRows.Count == 0 ? "ausente" : "duplicado", "1", summaryRows.Count.ToString()));
            else
            {
                var row = summaryRows[0];
                CompareText(summaryName + "/RUC emisor", voucher.IssuerTaxId, row.Cell(5).GetString());
                var issuerName = voucher.XmlData?.Fields.FirstOrDefault(f => f.Path.Contains("/infoTributaria[1]/", StringComparison.Ordinal) && f.Path.EndsWith("/razonSocial[1]", StringComparison.Ordinal))?.Value;
                CompareText(summaryName + "/emisor", issuerName, row.Cell(6).GetString());
                CompareText(summaryName + "/identificación receptor", voucher.Identification, row.Cell(7).GetString());
                CompareText(summaryName + "/receptor", voucher.Recipient, row.Cell(8).GetString());
                CompareNumber(summaryName + "/subtotal", voucher.Subtotal, row.Cell(9));
                CompareNumber(summaryName + "/impuestos", voucher.Taxes, row.Cell(10));
                CompareNumber(summaryName + "/total", voucher.Total, row.Cell(11));
                for (var i = 0; i < extras.Length; i++)
                {
                    var tag = extras[i]; var value = voucher.XmlData?.Fields.FirstOrDefault(f => f.Path.Contains("/" + section + "[1]/", StringComparison.Ordinal) && f.Path.EndsWith("/" + tag + "[1]", StringComparison.Ordinal))?.Value;
                    var cell = row.Cell(12 + i);
                    if (tag.StartsWith("fecha", StringComparison.Ordinal) && DateOnly.TryParseExact(value, ["dd/MM/yyyy", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expectedDate))
                    {
                        if (!cell.TryGetValue<DateTime>(out var actualDate) || DateOnly.FromDateTime(actualDate) != expectedDate)
                            differences.Add(new(summaryName + "/" + tag, cell.IsEmpty() ? "ausente" : "incorrecto", expectedDate.ToString("dd/MM/yyyy"), cell.IsEmpty() ? "[ausente]" : cell.GetFormattedString()));
                    }
                    else if (tag == "totalDescuento" && decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var discount)) CompareNumber(summaryName + "/" + tag, discount, cell);
                    else CompareText(summaryName + "/" + tag, value, cell.GetString());
                }
            }
        }
        var name = voucher.Type switch { "Facturas" => "XML Facturas", "Retenciones" => "XML Retenciones", "Notas-Credito" => "XML Notas crédito", "Notas-Debito" => "XML Notas débito", "Guias-Remision" => "XML Guías", "Liquidaciones" => "XML Liquidaciones", _ => "XML Otros" };
        var raw = workbook.Worksheets.FirstOrDefault(w => w.Name == name);
        var rawRows = raw?.RowsUsed().Skip(1).Where(r => r.Cell(1).GetString() == voucher.AccessKey).ToList() ?? [];
        var fields = rawRows.GroupBy(r => r.Cell(3).GetString()).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var field in voucher.XmlData?.Fields ?? [])
        {
            if (!fields.TryGetValue(field.Path, out var found)) { differences.Add(new(field.Path, "ausente", Mask(field.Value), "[ausente]")); continue; }
            if (found.Count != 1) { differences.Add(new(field.Path, "duplicado", "1", found.Count.ToString())); continue; }
            CompareText(field.Path, field.Value, found[0].Cell(4).GetString());
        }
        var itemsName = voucher.Type switch { "Facturas" => "Detalles facturas", "Retenciones" => "Detalles retenciones", "Notas-Credito" => "Detalles notas crédito", "Notas-Debito" => "Detalles notas débito", "Guias-Remision" => "Detalles guías", "Liquidaciones" => "Detalles liquidaciones", _ => "Detalles otros" };
        var itemSheet = workbook.Worksheets.FirstOrDefault(w => w.Name == itemsName);
        var itemRows = itemSheet?.RowsUsed().Skip(1).Where(r => r.Cell(1).GetString() == voucher.AccessKey).GroupBy(r => r.Cell(4).GetString()).ToDictionary(g => g.Key, g => g.ToList()) ?? [];
        foreach (var item in voucher.XmlData?.Items ?? [])
        {
            if (!itemRows.TryGetValue(item.Path, out var rows) || rows.Count != 1) { differences.Add(new(item.Path, rows is null ? "ausente" : "duplicado", "1", rows?.Count.ToString() ?? "0")); continue; }
            string? Value(params string[] names) => names.Select(n => item.Values.FirstOrDefault(f => f.Path == n)?.Value).FirstOrDefault(v => v is not null);
            CompareText(item.Path + "/código", Value("codigoPrincipal", "codigoInterno", "codigo"), rows[0].Cell(5).GetString());
            CompareText(item.Path + "/código auxiliar", Value("codigoAuxiliar", "codigoAdicional", "codigoPorcentaje"), rows[0].Cell(6).GetString());
            CompareText(item.Path + "/descripción", Value("descripcion", "razon"), rows[0].Cell(7).GetString());
            CompareText(item.Path + "/destinatario", Value("razonSocialDestinatario"), rows[0].Cell(18).GetString());
            CompareText(item.Path + "/identificación destinatario", Value("identificacionDestinatario"), rows[0].Cell(17).GetString());
            CompareText(item.Path + "/documento sustento", Value("numDocSustento"), rows[0].Cell(23).GetString());
            foreach (var (tag, column) in new[] { ("cantidad", 8), ("precioUnitario", 9), ("descuento", 10), ("precioTotalSinImpuesto", 11), ("baseImponible", 12), ("tarifa", 13) })
            {
                var rawValue = item.Values.FirstOrDefault(v => v.Path == tag)?.Value;
                if (decimal.TryParse(rawValue, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                    CompareNumber(item.Path + "/" + tag, amount, rows[0].Cell(column));
            }
            var taxValue = item.Values.FirstOrDefault(v => v.Path == "valor")?.Value;
            if (item.Kind is "impuesto" or "totalImpuesto" or "motivo" && decimal.TryParse(taxValue, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                CompareNumber(item.Path + "/valor", parsed, rows[0].Cell(item.Kind == "motivo" ? 21 : 14));
        }
        if (voucher.Type == "Retenciones" && voucher.XmlData?.Retentions.Count > 0)
        {
            var retention = workbook.Worksheets.FirstOrDefault(w => w.Name == "Retenciones");
            var lines = retention?.RowsUsed().Skip(1).Where(r => r.Cell(13).GetString() == voucher.AccessKey).ToList() ?? [];
            if (lines.Count != voucher.XmlData.Retentions.Count)
                differences.Add(new("Retenciones: número de líneas", lines.Count == 0 ? "ausente" : "incorrecto", voucher.XmlData.Retentions.Count.ToString(), lines.Count.ToString()));
            for (var i = 0; i < Math.Min(lines.Count, voucher.XmlData.Retentions.Count); i++)
            {
                var line = voucher.XmlData.Retentions[i]; var offset = line.TaxCode == "1" ? 3 : line.TaxCode == "2" ? 6 : 14;
                CompareText($"Retención {i + 1}: código de impuesto", line.TaxCode, lines[i].Cell(10).GetString());
                CompareText($"Retención {i + 1}: código de retención", line.RetentionCode, lines[i].Cell(11).GetString());
                CompareText($"Retención {i + 1}: documento sustento", line.SupportNumber, lines[i].Cell(12).GetString());
                CompareNumber($"Retención {i + 1}: base", line.Base, lines[i].Cell(offset));
                CompareNumber($"Retención {i + 1}: porcentaje", line.Rate, lines[i].Cell(offset + 1));
                CompareNumber($"Retención {i + 1}: valor", line.Amount, lines[i].Cell(offset + 2));
            }
        }
        return differences;
    }
}
