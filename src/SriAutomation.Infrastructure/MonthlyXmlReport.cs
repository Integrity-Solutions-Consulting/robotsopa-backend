using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

// Each voucher is shown once in the summary and type sheets. Repeating XML
// elements are kept in a separate, indexed tree, never summed a second time.
internal static class MonthlyXmlReport
{
    private sealed record Field(string Header, string Parent, string Name, bool Number = false);
    private static readonly Dictionary<string,(string Sheet,string Info,Field[] Fields)> Types = new()
    {
        ["Facturas"] = ("Facturas", "infoFactura", [new("Dirección establecimiento", "infoFactura", "dirEstablecimiento"), new("Total sin impuestos", "infoFactura", "totalSinImpuestos", true), new("Total descuento", "infoFactura", "totalDescuento", true), new("Propina", "infoFactura", "propina", true), new("Importe total", "infoFactura", "importeTotal", true), new("Moneda", "infoFactura", "moneda")]),
        ["Retenciones"] = ("Retenciones", "infoCompRetencion", [new("Período fiscal", "infoCompRetencion", "periodoFiscal"), new("Tipo identificación retenido", "infoCompRetencion", "tipoIdentificacionSujetoRetenido"), new("Parte relacionada", "infoCompRetencion", "parteRel"), new("Contribuyente especial", "infoCompRetencion", "contribuyenteEspecial")]),
        ["Notas-Credito"] = ("Notas de crédito", "infoNotaCredito", [new("Documento modificado", "infoNotaCredito", "codDocModificado"), new("Número modificado", "infoNotaCredito", "numDocModificado"), new("Fecha documento modificado", "infoNotaCredito", "fechaEmisionDocSustento"), new("Motivo", "infoNotaCredito", "motivo"), new("Total sin impuestos", "infoNotaCredito", "totalSinImpuestos", true), new("Valor modificación", "infoNotaCredito", "valorModificacion", true), new("Moneda", "infoNotaCredito", "moneda")]),
        ["Notas-Debito"] = ("Notas de débito", "infoNotaDebito", [new("Documento modificado", "infoNotaDebito", "codDocModificado"), new("Número modificado", "infoNotaDebito", "numDocModificado"), new("Fecha documento modificado", "infoNotaDebito", "fechaEmisionDocSustento"), new("Total sin impuestos", "infoNotaDebito", "totalSinImpuestos", true), new("Valor total", "infoNotaDebito", "valorTotal", true)]),
        ["Liquidaciones"] = ("Liquidaciones de compra", "infoLiquidacionCompra", [new("Dirección establecimiento", "infoLiquidacionCompra", "dirEstablecimiento"), new("Total sin impuestos", "infoLiquidacionCompra", "totalSinImpuestos", true), new("Total descuento", "infoLiquidacionCompra", "totalDescuento", true), new("Importe total", "infoLiquidacionCompra", "importeTotal", true), new("Moneda", "infoLiquidacionCompra", "moneda")])
    };
    private static readonly string[] Common = ["Tipo", "Número", "Clave de acceso", "Fecha de emisión", "Razón social receptor", "Identificación receptor", "Autorización", "Subtotal", "Impuestos", "Total", "Razón social emisor", "RUC emisor", "Descuento", "Fecha de autorización"];
    private static readonly HashSet<string> Numeric = new(StringComparer.Ordinal) { "cantidad", "precioUnitario", "precioSinSubsidio", "descuento", "precioTotalSinImpuesto", "baseImponible", "tarifa", "valor", "valorRetenido", "porcentajeRetener", "porcentaje", "totalSinImpuestos", "totalDescuento", "importeTotal", "valorTotal", "valorModificacion", "propina" };
    private sealed record XmlState(string Status, XElement? Root, Voucher? Parsed = null);

    public static void Add(XLWorkbook book, IReadOnlyList<Voucher> rows, Func<string,string> resolve)
    {
        book.Worksheet("Resumen").Name = "Totales";
        var summary = book.AddWorksheet("Resumen"); summary.Position = 1;
        var lines = book.AddWorksheet("Productos y motivos");
        var taxes = book.AddWorksheet("Impuestos y retenciones");
        Headers(summary, Common);
        Headers(lines, ["Tipo", "Clave de acceso", "Número de línea", "Código principal", "Código auxiliar", "Descripción o motivo", "Cantidad", "Precio unitario", "Descuento", "Subtotal sin impuestos", "Valor motivo"]);
        Headers(taxes, ["Tipo", "Clave de acceso", "Origen", "Número de línea", "Documento sustento (tipo)", "Documento sustento (número)", "Código impuesto", "Código porcentaje o retención", "Tarifa o porcentaje", "Base imponible", "Valor impuesto", "Valor retenido"]);
        var sheets = new Dictionary<string,IXLWorksheet>();
        foreach (var group in rows.GroupBy(x => x.Type))
        {
            var sheetName = Types.TryGetValue(group.Key, out var spec) ? spec.Sheet : group.Key;
            if (sheetName.Length > 31 || book.Worksheets.Any(w => w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))) continue;
            var ws = book.AddWorksheet(sheetName); sheets[group.Key] = ws;
            Headers(ws, Common.Concat(spec.Fields?.Select(f => f.Header) ?? []).ToArray());
        }
        var sr = 2; var lr = 2; var tr = 2; var totals = new Dictionary<string,List<decimal?>>();
        foreach (var d in rows)
        {
            var xml = ReadXml(d, resolve);
            if (xml.Root is null && d.Status == "ValidadoXML")
            {
                var pending = book.Worksheet("Pendientes");
                var pendingRow = (pending.LastRowUsed()?.RowNumber() ?? 1) + 1;
                pending.Cell(pendingRow, 1).Value = d.Number ?? d.AccessKey;
                pending.Cell(pendingRow, 2).Value = "XML no verificable";
                pending.Cell(pendingRow, 3).Value = xml.Status;
            }
            if (!totals.TryGetValue(d.Type, out var values)) totals[d.Type] = values = [];
            values.Add(xml.Parsed?.Total ?? d.Total);
            WriteCommon(summary, sr++, d, xml);
            if (sheets.TryGetValue(d.Type, out var ws))
            {
                var row = (ws.LastRowUsed()?.RowNumber() ?? 1) + 1;
                WriteCommon(ws, row, d, xml);
                if (Types.TryGetValue(d.Type, out var spec))
                    for (var i = 0; i < spec.Fields.Length; i++) WriteField(ws.Cell(row, Common.Length + i + 1), xml, spec.Fields[i]);
            }
            if (xml.Root is not null)
            {
                foreach (var container in xml.Root.Descendants().Where(e => e.Name.LocalName is "detalles" or "motivos"))
                {
                    var index = 0;
                    foreach (var line in container.Elements().Where(e => e.Name.LocalName is "detalle" or "motivo"))
                    {
                        index++; var row = lr++; Text(lines.Cell(row, 1), d.Type); Text(lines.Cell(row, 2), d.AccessKey); lines.Cell(row, 3).Value = index;
                        ElementText(lines.Cell(row, 4), line, "codigoPrincipal"); ElementText(lines.Cell(row, 5), line, "codigoAuxiliar");
                        ElementText(lines.Cell(row, 6), line, line.Name.LocalName == "motivo" ? "razon" : "descripcion");
                        ElementNumber(lines.Cell(row, 7), line, "cantidad"); ElementNumber(lines.Cell(row, 8), line, "precioUnitario"); ElementNumber(lines.Cell(row, 9), line, "descuento");
                        ElementNumber(lines.Cell(row, 10), line, "precioTotalSinImpuesto"); ElementNumber(lines.Cell(row, 11), line, "valor");
                    }
                }
                foreach (var tax in xml.Root.Descendants().Where(e => e.Name.LocalName is "impuesto" or "totalImpuesto" or "retencion" or "impuestoDocSustento"))
                {
                    var row = tr++; Text(taxes.Cell(row, 1), d.Type); Text(taxes.Cell(row, 2), d.AccessKey);
                    var line = tax.Ancestors().FirstOrDefault(e => e.Name.LocalName == "detalle" && e.Parent?.Name.LocalName == "detalles");
                    var support = tax.Ancestors().FirstOrDefault(e => e.Name.LocalName == "docSustento");
                    taxes.Cell(row, 3).Value = tax.Name.LocalName == "impuestoDocSustento" ? "Impuesto documento sustento" : tax.Name.LocalName == "retencion" || d.Type == "Retenciones" ? "Retención" : line is not null ? "Impuesto de detalle" : "Impuesto del comprobante";
                    if (line is not null) taxes.Cell(row, 4).Value = line.ElementsBeforeSelf(line.Name).Count() + 1; else taxes.Cell(row, 4).Value = "No aplica";
                    ElementText(taxes.Cell(row, 5), support ?? tax, "codDocSustento");
                    ElementText(taxes.Cell(row, 6), support ?? tax, "numDocSustento");
                    ElementText(taxes.Cell(row, 7), tax, tax.Name.LocalName == "impuestoDocSustento" ? "codImpuestoDocSustento" : "codigo"); ElementText(taxes.Cell(row, 8), tax, tax.Elements().Any(e => e.Name.LocalName == "codigoRetencion") ? "codigoRetencion" : "codigoPorcentaje");
                    ElementNumber(taxes.Cell(row, 9), tax, tax.Elements().Any(e => e.Name.LocalName == "porcentajeRetener") ? "porcentajeRetener" : "tarifa");
                    ElementNumber(taxes.Cell(row, 10), tax, "baseImponible"); ElementNumber(taxes.Cell(row, 11), tax, tax.Name.LocalName == "impuestoDocSustento" ? "valorImpuesto" : "valor"); ElementNumber(taxes.Cell(row, 12), tax, "valorRetenido");
                }

            }
        }
        var totalSheet = book.Worksheet("Totales");
        for (var row = 2; row <= (totalSheet.LastRowUsed()?.RowNumber() ?? 1); row++)
        {
            var values = totals[totalSheet.Cell(row, 1).GetString()];
            if (values.Any(v => v.HasValue)) totalSheet.Cell(row, 3).Value = values.Where(v => v.HasValue).Sum(v => v!.Value);
            else totalSheet.Cell(row, 3).Clear();
            totalSheet.Cell(row, 4).Value = values.Count(v => !v.HasValue);
        }
        Format(summary); Format(lines); Format(taxes); foreach (var ws in sheets.Values) Format(ws);
    }
    private static XmlState ReadXml(Voucher d, Func<string,string> resolve)
    {
        if (string.IsNullOrWhiteSpace(d.XmlPath)) return new("No disponible", null);
        try
        {
            var path = resolve(d.XmlPath); if (!File.Exists(path)) return new("No disponible", null);
            if (new FileInfo(path).Length > 20_000_000) return new("XML inválido", null);
            var bytes = File.ReadAllBytes(path);
            if (!string.IsNullOrEmpty(d.XmlSha256) && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), d.XmlSha256, StringComparison.OrdinalIgnoreCase)) return new("Integridad no confirmada", null);
            var parsed = new XmlDocumentParser().ParseXml(d.Account, bytes);
            if (parsed.Type != d.Type || parsed.AccessKey != d.AccessKey || parsed.Issued != d.Issued) return new("Identidad no coincide", null);
            static XDocument Load(string value) { using var r = XmlReader.Create(new StringReader(value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 }); return XDocument.Load(r); }
            var doc = Load(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            var embedded = doc.Root?.Name.LocalName == "autorizacion" ? doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "comprobante")?.Value : null;
            var root = embedded is null ? doc.Root : Load(embedded).Root;
            return root is null ? new("XML inválido", null) : new("Disponible", root, parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or XmlException or InvalidOperationException or FormatException) { return new("XML inválido", null); }
    }
    private static void Headers(IXLWorksheet ws, IReadOnlyList<string> headers) { for (var i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i]; }
    private static void Text(IXLCell cell, string? text) { cell.Style.NumberFormat.Format = "@"; cell.Value = string.IsNullOrEmpty(text) ? "No disponible" : text; }
    private static void Amount(IXLCell cell, decimal? value) { if (value.HasValue) { cell.Value = value.Value; cell.Style.NumberFormat.Format = "#,##0.00"; } else cell.Value = "No disponible"; }
    private static void ElementText(IXLCell cell, XElement parent, string name)
    {
        var element = parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (element is null) cell.Value = "No consta en XML"; else Text(cell, element.Value);
    }
    private static void ElementNumber(IXLCell cell, XElement parent, string name)
    {
        var element = parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (element is null) { cell.Value = "No consta en XML"; return; }
        if (decimal.TryParse(element.Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) { cell.Value = value; cell.Style.NumberFormat.Format = "#,##0.00######"; }
        else cell.Value = "No interpretable";
    }
    private static void WriteCommon(IXLWorksheet ws, int row, Voucher d, XmlState xml)
    {
        var parsed = xml.Parsed;
        Text(ws.Cell(row, 1), d.Type); Text(ws.Cell(row, 2), parsed?.Number ?? d.Number); Text(ws.Cell(row, 3), d.AccessKey);
        ws.Cell(row, 4).Value = d.Issued.ToDateTime(TimeOnly.MinValue); ws.Cell(row, 4).Style.DateFormat.Format = "dd/mm/yyyy";
        Text(ws.Cell(row, 5), parsed?.Recipient ?? d.Recipient); Text(ws.Cell(row, 6), parsed?.Identification ?? d.Identification); Text(ws.Cell(row, 7), parsed?.Authorization ?? d.Authorization);
        Amount(ws.Cell(row, 8), parsed?.Subtotal ?? d.Subtotal); Amount(ws.Cell(row, 9), parsed?.Taxes ?? d.Taxes); Amount(ws.Cell(row, 10), parsed?.Total ?? d.Total);
        Text(ws.Cell(row, 11), parsed?.IssuerName ?? d.IssuerName); Text(ws.Cell(row, 12), parsed?.IssuerTaxId ?? d.IssuerTaxId);
        Amount(ws.Cell(row, 13), parsed?.Discount ?? d.Discount);
        var authorizedAt = parsed?.AuthorizedAt ?? d.AuthorizedAt;
        if (DateTimeOffset.TryParse(authorizedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        { ws.Cell(row, 14).Value = date.DateTime; ws.Cell(row, 14).Style.DateFormat.Format = "dd/mm/yyyy hh:mm:ss"; }
        else Text(ws.Cell(row, 14), authorizedAt);
    }
    private static void WriteField(IXLCell cell, XmlState xml, Field field)
    {
        if (xml.Root is null) { cell.Value = "No disponible"; return; }
        var parent = xml.Root.Elements().FirstOrDefault(e => e.Name.LocalName == field.Parent);
        var element = parent?.Elements().FirstOrDefault(e => e.Name.LocalName == field.Name);
        if (element is null) { cell.Value = "No consta en XML"; return; }
        if (field.Number) { if (decimal.TryParse(element.Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) Amount(cell, n); else cell.Value = "No interpretable"; return; }
        if (field.Name.StartsWith("fecha", StringComparison.Ordinal) && DateTime.TryParseExact(element.Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { cell.Value = date; cell.Style.DateFormat.Format = "dd/mm/yyyy"; return; }
        Text(cell, element.Value);
    }
    private static void Format(IXLWorksheet ws)
    {
        var used = ws.RangeUsed()!; used.Style.Font.FontName = "Calibri"; used.Style.Font.FontSize = 11;
        ws.Row(1).Style.Font.Bold = true; ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#15334D"); ws.Row(1).Style.Font.FontColor = XLColor.White;
        ws.Row(1).Height = 30; ws.Row(1).Style.Alignment.WrapText = true; ws.SheetView.FreezeRows(1); used.SetAutoFilter(); ws.Columns().AdjustToContents();
        foreach (var column in ws.ColumnsUsed()) column.Width = Math.Min(60, Math.Max(12, column.Width + 2));
    }
}
