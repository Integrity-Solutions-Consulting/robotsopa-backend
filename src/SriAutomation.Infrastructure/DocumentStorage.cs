using ClosedXML.Excel;
using System.Globalization;
using System.Text.RegularExpressions;
using SriAutomation.Application;
using SriAutomation.Domain;
using UglyToad.PdfPig;

namespace SriAutomation.Infrastructure;

public sealed class DocumentStorage : IStorageProvider
{
    private readonly string root;
    private readonly Func<string?> subfolder;
    private static readonly string[] Months = ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"];
    public DocumentStorage(string root,Func<string?>? subfolder = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("La ruta interna debe ser absoluta.");
        this.root = Path.GetFullPath(root);
        this.subfolder=subfolder??(()=>null);
    }
    public void CheckAccess()
    {
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, ".probe-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "ok"); if (File.ReadAllText(probe) != "ok") throw new IOException("Sin acceso de lectura."); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }
    public string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new ArgumentException("Ruta relativa requerida.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Ruta fuera del destino.");
        // No seguir enlaces que permitan salir del volumen autorizado.
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null && parent.FullName.Length >= root.Length; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Enlaces de directorio no permitidos.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Enlaces de archivo no permitidos.");
        return path;
    }
    public string AccountDirectory(string account) {var prefix=subfolder();return string.IsNullOrWhiteSpace(prefix)?Validation.Identifier(account):Path.Combine(Validation.Identifier(prefix),Validation.Identifier(account));}
    private string Month(string account, int year, int month) => Path.Combine(year.ToString("D4"), $"{month:D2}-{Months[month - 1]}", AccountDirectory(account));
    public string Save(Voucher document, string extension, byte[] content)
    {
        if (!Validation.Types.Contains(document.Type) || extension is not "xml" and not "pdf") throw new ArgumentException("Tipo o extensión inválidos.");
        if (content.Length == 0 || content.Length > 20_000_000) throw new ArgumentException("Archivo vacío o demasiado grande.");
        if (extension == "pdf")
        {
            try
            {
                using var pdf = PdfDocument.Open(content);
                if (pdf.NumberOfPages == 0) throw new ArgumentException("PDF sin páginas.");
            }
            catch (UglyToad.PdfPig.Core.PdfDocumentFormatException) { throw new ArgumentException("El archivo no es un PDF válido."); }
        }
        else _ = new XmlDocumentParser().ParseXml(document.Account, content);
        var relative = Path.Combine(Month(document.Account, document.Issued.Year, document.Issued.Month), document.Type, Validation.Identifier(document.AccessKey) + "." + extension);
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(content); file.Flush(true); }
            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path))
            { if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) throw new ArgumentException("Ya existe contenido distinto para esta clave."); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return relative;
    }
    private static bool TryAuthorizationDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Interpretar la fecha escrita en el comprobante sin convertirla a otra zona horaria.
        var match = Regex.Match(value.Trim(), @"^(?<date>\d{4}-\d{2}-\d{2}|\d{2}/\d{2}/\d{4})(?:[T ](?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)(?<zone>Z|[+-]\d{2}:\d{2})?)?$");
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["date"].Value, ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        if (match.Groups["time"].Success && !TimeOnly.TryParseExact(match.Groups["time"].Value, ["HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        var zone = match.Groups["zone"].Value;
        if (zone.Length > 1 && (!int.TryParse(zone.AsSpan(1, 2), out var hours) || !int.TryParse(zone.AsSpan(4, 2), out var minutes) || hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))) return false;
        return true;
    }
    public string Report(string account, int year, int month, IEnumerable<Voucher> documents)
    {
        _ = new DateOnly(year, month, 1);
        var directory = Resolve(Path.Combine(year.ToString("D4"), ".report-" + Validation.Identifier(account)));
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        using var gate = new FileStream(directory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var rows = RestoreXmlData(this, documents.Where(d => d.Account == account && d.Issued.Year == year && d.Issued.Month == month))
            .DistinctBy(d => (d.Type, d.AccessKey)).OrderBy(d => d.Issued).ThenBy(d => d.Number).ToList();
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Documentos");
        string[] headers = ["Tipo de comprobante", "Número de documento", "Número de autorización", "Fecha de autorización", "Razón social", "Identificación", "Subtotal", "Total", "Fecha", "Hora"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var d in rows)
        {
            sheet.Cell(row, 1).Value = d.Type; sheet.Cell(row, 2).Value = d.Number;
            sheet.Cell(row, 3).Value = d.Authorization;
            if (TryAuthorizationDate(d.AuthorizedAt, out var authorizationDate))
                sheet.Cell(row, 4).Value = authorizationDate.ToDateTime(TimeOnly.MinValue);
            else
                System.Diagnostics.Trace.TraceWarning("Fecha de autorización ausente o inválida al generar el reporte.");
            sheet.Cell(row, 5).Value = d.Recipient; sheet.Cell(row, 6).Value = d.Identification;
            if (d.Subtotal.HasValue) sheet.Cell(row, 7).Value = d.Subtotal.Value;
            if (d.Total.HasValue) sheet.Cell(row, 8).Value = d.Total.Value;
            sheet.Cell(row, 9).Value = d.Issued.ToDateTime(TimeOnly.MinValue);
            var authorization = d.AuthorizedAt?.Trim();
            if (TryAuthorizationDate(authorization, out _) && authorization?.Length >= 19 &&
                TimeOnly.TryParseExact(authorization.Substring(11, 8), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                sheet.Cell(row, 10).Value = time.ToTimeSpan();
            row++;
        }
        sheet.Columns(7, 8).Style.NumberFormat.Format = "#,##0.00";
        sheet.Column(4).Style.NumberFormat.Format = "dd/MM/yyyy";
        sheet.Column(9).Style.NumberFormat.Format = "dd/MM/yyyy";
        sheet.Column(10).Style.NumberFormat.Format = "hh:mm:ss";
        foreach (var column in new[] { 2, 3, 6 }) sheet.Column(column).Style.NumberFormat.Format = "@";
        var summary = book.AddWorksheet("Resumen");
        string[] summaryHeaders = ["Tipo", "Documentos", "Total del tipo", "Sin importe"];
        for (var i = 0; i < summaryHeaders.Length; i++) summary.Cell(1, i + 1).Value = summaryHeaders[i];
        row = 2;
        foreach (var group in rows.GroupBy(d => d.Type))
        {
            summary.Cell(row, 1).Value = group.Key; summary.Cell(row, 2).Value = group.Count();
            if (group.Any(d => d.Total.HasValue)) summary.Cell(row, 3).Value = group.Sum(d => d.Total ?? 0);
            summary.Cell(row++, 4).Value = group.Count(d => !d.Total.HasValue);
        }
        summary.Column(3).Style.NumberFormat.Format = "#,##0.00";
        var errors = book.AddWorksheet("Pendientes");
        errors.Cell(1, 1).Value = "Número de comprobante"; errors.Cell(1, 2).Value = "Estado"; errors.Cell(1, 3).Value = "Motivo";
        row = 2;
        foreach (var d in rows.Where(d => d.Status != "ValidadoXML"))
        { errors.Cell(row, 1).Value = d.Number; errors.Cell(row, 2).Value = d.Status; errors.Cell(row++, 3).Value = d.Warning; }
        AddXmlDetailSheets(book, rows);
        AddTypeSummaries(book, rows);
        AddItemSheets(book, rows);
        foreach (var ws in book.Worksheets)
        {
            var used = ws.RangeUsed()!;
            used.Style.Font.FontName = "Calibri"; used.Style.Font.FontSize = 11;
            used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(1).Style.Font.Bold = true; ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#15334D"); ws.Row(1).Style.Font.FontColor = XLColor.White;
            ws.Row(1).Height = 24;
            ws.SheetView.FreezeRows(1); used.SetAutoFilter(); ws.Columns().AdjustToContents(12, 55);
            foreach (var column in ws.ColumnsUsed()) column.Width = Math.Min(60, column.Width + 2);
            for (var r = 2; r <= used.LastRow().RowNumber(); r++)
            {
                ws.Row(r).Height = 20;
                if (r % 2 == 0) ws.Range(r, 1, r, used.LastColumn().ColumnNumber()).Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F7FA");
            }
        }
        // Keep document rows at a standard height and make columns wider.
        sheet.Columns().AdjustToContents(12, 100);
        foreach (var column in sheet.ColumnsUsed()) column.Width = Math.Min(120, Math.Max(28, column.Width + 6));
        sheet.Column(3).Width = Math.Max(55, sheet.Column(3).Width);
        sheet.Column(4).Width = Math.Max(55, sheet.Column(4).Width);
        sheet.Column(5).Style.Alignment.WrapText = true;
        for (var r = 2; r <= sheet.LastRowUsed()!.RowNumber(); r++)
            sheet.Row(r).Height = Math.Min(409, Math.Max(20, 15 * Math.Ceiling(sheet.Cell(r, 5).GetString().Length / Math.Max(1, sheet.Column(5).Width - 4))));
        errors.Column(3).Style.Alignment.WrapText = true;
        for (var r = 2; r <= errors.LastRowUsed()!.RowNumber(); r++)
            errors.Row(r).Height = Math.Min(409, Math.Max(20, 15 * Math.Ceiling(errors.Cell(r, 3).GetString().Length / Math.Max(1, errors.Column(3).Width - 4))));
        var relative = Path.Combine(AccountDirectory(account), year.ToString("D4"), $"{month:D2}-{Months[month - 1]}", $"Resumen_{year:D4}_{month:D2}.xlsx");
        var path = Resolve(relative); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { book.SaveAs(file); file.Flush(true); }
            // Reopen the exact bytes to publish before touching the previous report.
            using (var input = File.OpenRead(temporary))
            using (var check = new XLWorkbook(input))
            {
                var documentSheet = check.Worksheet("Documentos");
                if (documentSheet.Cell(1, 9).GetString() != "Fecha" || documentSheet.Cell(1, 10).GetString() != "Hora" ||
                    documentSheet.LastRowUsed()?.RowNumber() != Math.Max(1, rows.Count + 1))
                    throw new InvalidDataException("El reporte temporal no supera la validación de estructura.");
            }
            if (File.Exists(path))
            {
                var versionDirectory = Resolve(Path.Combine(AccountDirectory(account), year.ToString("D4"), $"{month:D2}-{Months[month - 1]}", ".versiones"));
                Directory.CreateDirectory(versionDirectory);
                var backup = Path.Combine(versionDirectory, $"Resumen_{year:D4}_{month:D2}_{DateTime.UtcNow:yyyyMMddTHHmmssfffffff}_{Guid.NewGuid():N}.xlsx");
                // Replace and backup are one filesystem operation; if unsupported, leave the old report intact.
                File.Replace(temporary, path, backup);
            }
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return relative;
    }

    internal static IEnumerable<Voucher> RestoreXmlData(IStorageProvider storage, IEnumerable<Voucher> documents)
    {
        foreach (var document in documents)
        {
            if (document.XmlData is not null || document.XmlPath is null) { yield return document; continue; }
            var path = storage.Resolve(document.XmlPath);
            if (!File.Exists(path)) { yield return document; continue; }
            var bytes = File.ReadAllBytes(path);
            if (document.XmlSha256 is not null && DownloadPipeline.Hash(bytes) != document.XmlSha256)
                throw new InvalidDataException("El XML almacenado no coincide con su huella.");
            var parsed = new XmlDocumentParser().ParseXml(document.Account, bytes);
            if (parsed.AccessKey != document.AccessKey || parsed.Type != document.Type || parsed.Issued != document.Issued)
                throw new InvalidDataException("El XML almacenado no coincide con el documento indexado.");
            parsed.Id = document.Id; parsed.Source = document.Source;
            parsed.XmlPath = document.XmlPath; parsed.XmlSha256 = document.XmlSha256;
            parsed.PdfPath = document.PdfPath; parsed.PdfSha256 = document.PdfSha256;
            yield return parsed;
        }
    }

    private static void AddXmlDetailSheets(XLWorkbook book, List<Voucher> documents)
    {
        // Preserve every XML leaf with its indexed path so repeated elements remain distinguishable.
        foreach (var group in documents.Where(d => d.XmlData is not null).GroupBy(d => d.Type))
        {
            var name = group.Key switch
            {
                "Facturas" => "XML Facturas", "Retenciones" => "XML Retenciones",
                "Notas-Credito" => "XML Notas crédito", "Notas-Debito" => "XML Notas débito",
                "Guias-Remision" => "XML Guías", "Liquidaciones" => "XML Liquidaciones", _ => "XML Otros"
            };
            var sheet = book.AddWorksheet(name);
            sheet.Cell(1, 1).Value = "Clave de acceso"; sheet.Cell(1, 2).Value = "Número";
            sheet.Cell(1, 3).Value = "Ruta XML"; sheet.Cell(1, 4).Value = "Valor XML";
            var row = 2;
            foreach (var document in group)
                foreach (var field in document.XmlData!.Fields)
                {
                    sheet.Cell(row, 1).Value = document.AccessKey; sheet.Cell(row, 2).Value = document.Number;
                    sheet.Cell(row, 3).Value = field.Path; sheet.Cell(row, 4).Value = field.Value;
                    row++;
                }
            sheet.Columns(1, 4).Style.NumberFormat.Format = "@";
        }
        var retained = documents.Where(d => d.Type == "Retenciones" && d.XmlData?.Retentions.Count > 0).ToList();
        if (retained.Count == 0) return;
        var ret = book.AddWorksheet("Retenciones");
        string[] headers = ["Fecha", "N.º comprobante retención", "Base imponible fuente", "% retención fuente", "Valor retenido fuente", "Base imponible IVA", "% retención IVA", "Valor retenido IVA", "Total retenido", "Código impuesto", "Código retención", "Documento sustento", "Clave de acceso", "Base otros impuestos", "% otros impuestos", "Valor retenido otros impuestos", "Período fiscal", "RUC emisor", "Emisor", "Identificación sujeto retenido", "Sujeto retenido", "Tipo documento sustento", "Fecha documento sustento", "Autorización"];
        for (var i = 0; i < headers.Length; i++) ret.Cell(1, i + 1).Value = headers[i];
        var r = 2;
        foreach (var doc in retained)
        {
            var first = true;
            foreach (var line in doc.XmlData!.Retentions)
            {
                string? Field(string section, string tag) => doc.XmlData.Fields.FirstOrDefault(f => f.Path.Contains("/" + section + "[1]/", StringComparison.Ordinal) && f.Path.EndsWith("/" + tag + "[1]", StringComparison.Ordinal))?.Value;
                ret.Cell(r, 1).Value = doc.Issued.ToDateTime(TimeOnly.MinValue);
                ret.Cell(r, 2).Value = doc.Number;
                var offset = line.TaxCode == "1" ? 3 : line.TaxCode == "2" ? 6 : 0;
                if (offset > 0)
                {
                    if (line.Base.HasValue) ret.Cell(r, offset).Value = line.Base.Value;
                    if (line.Rate.HasValue) ret.Cell(r, offset + 1).Value = line.Rate.Value;
                    if (line.Amount.HasValue) ret.Cell(r, offset + 2).Value = line.Amount.Value;
                }
                if (first && doc.Total.HasValue) ret.Cell(r, 9).Value = doc.Total.Value;
                ret.Cell(r, 10).Value = line.TaxCode; ret.Cell(r, 11).Value = line.RetentionCode;
                ret.Cell(r, 12).Value = line.SupportNumber; ret.Cell(r, 13).Value = doc.AccessKey;
                if (offset == 0)
                {
                    if (line.Base.HasValue) ret.Cell(r, 14).Value = line.Base.Value;
                    if (line.Rate.HasValue) ret.Cell(r, 15).Value = line.Rate.Value;
                    if (line.Amount.HasValue) ret.Cell(r, 16).Value = line.Amount.Value;
                }
                ret.Cell(r, 17).Value = Field("infoCompRetencion", "periodoFiscal"); ret.Cell(r, 18).Value = doc.IssuerTaxId;
                ret.Cell(r, 19).Value = Field("infoTributaria", "razonSocial"); ret.Cell(r, 20).Value = doc.Identification;
                ret.Cell(r, 21).Value = doc.Recipient; ret.Cell(r, 22).Value = line.SupportCode;
                if (DateOnly.TryParseExact(line.SupportDate, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var supportDate))
                { ret.Cell(r, 23).Value = supportDate.ToDateTime(TimeOnly.MinValue); ret.Cell(r, 23).Style.NumberFormat.Format = "dd/MM/yyyy"; }
                ret.Cell(r, 24).Value = doc.Authorization;
                first = false; r++;
            }
        }
        ret.Column(1).Style.NumberFormat.Format = "dd/MM/yyyy";
        ret.Columns(3, 9).Style.NumberFormat.Format = "#,##0.00";
        ret.Columns(14, 16).Style.NumberFormat.Format = "#,##0.00";
        ret.Column(2).Style.NumberFormat.Format = "@"; ret.Columns(10, 13).Style.NumberFormat.Format = "@";
        var totals = book.AddWorksheet("Resumen retenciones");
        string[] totalHeaders = ["Concepto", "Porcentaje", "Valor retenido"];
        for (var i = 0; i < totalHeaders.Length; i++) totals.Cell(1, i + 1).Value = totalHeaders[i];
        var last = r - 1;
        totals.Cell(2, 1).Value = "Fuente visible";
        totals.Cell(2, 3).FormulaA1 = $"SUBTOTAL(109,'Retenciones'!E2:E{last})";
        totals.Cell(3, 1).Value = "IVA visible";
        totals.Cell(3, 3).FormulaA1 = $"SUBTOTAL(109,'Retenciones'!H2:H{last})";
        totals.Cell(4, 1).Value = "Otros impuestos visibles";
        totals.Cell(4, 3).FormulaA1 = $"SUBTOTAL(109,'Retenciones'!P2:P{last})";
        var summaryRow = 5;
        foreach (var tax in new[] { (Code: "1", Label: "Fuente", RateColumn: "D", AmountColumn: "E"), (Code: "2", Label: "IVA", RateColumn: "G", AmountColumn: "H") })
        {
            foreach (var rate in retained.SelectMany(d => d.XmlData!.Retentions).Where(line => line.TaxCode == tax.Code && line.Rate.HasValue).Select(line => line.Rate!.Value).Distinct().Order())
            {
                totals.Cell(summaryRow, 1).Value = tax.Label + " (todas las filas)";
                totals.Cell(summaryRow, 2).Value = rate;
                totals.Cell(summaryRow, 3).FormulaA1 = $"SUMIF('Retenciones'!{tax.RateColumn}$2:{tax.RateColumn}${last},B{summaryRow},'Retenciones'!{tax.AmountColumn}$2:{tax.AmountColumn}${last})";
                summaryRow++;
            }
        }
        totals.Columns(2, 3).Style.NumberFormat.Format = "#,##0.00####";
    }

    private static void AddTypeSummaries(XLWorkbook book, List<Voucher> documents)
    {
        foreach (var group in documents.Where(d => d.XmlData is not null && d.Type != "Retenciones").GroupBy(d => d.Type))
        {
            var (name, info, extra) = group.Key switch
            {
                "Facturas" => ("Facturas", "infoFactura", new[] { ("Moneda", "moneda"), ("Descuento", "totalDescuento"), ("Dirección comprador", "direccionComprador") }),
                "Liquidaciones" => ("Liquidaciones", "infoLiquidacionCompra", new[] { ("Moneda", "moneda"), ("Descuento", "totalDescuento"), ("Dirección proveedor", "direccionProveedor") }),
                "Notas-Credito" => ("Notas crédito", "infoNotaCredito", new[] { ("Tipo documento modificado", "codDocModificado"), ("Documento modificado", "numDocModificado"), ("Fecha documento modificado", "fechaEmisionDocSustento"), ("Motivo", "motivo") }),
                "Notas-Debito" => ("Notas débito", "infoNotaDebito", new[] { ("Tipo documento modificado", "codDocModificado"), ("Documento modificado", "numDocModificado"), ("Fecha documento modificado", "fechaEmisionDocSustento") }),
                "Guias-Remision" => ("Guías remisión", "infoGuiaRemision", new[] { ("Transportista", "razonSocialTransportista"), ("Identificación transportista", "rucTransportista"), ("Punto de partida", "dirPartida"), ("Inicio transporte", "fechaIniTransporte"), ("Fin transporte", "fechaFinTransporte"), ("Placa", "placa") }),
                _ => ("Otros comprobantes", "", Array.Empty<(string, string)>())
            };
            var sheet = book.AddWorksheet(name);
            string[] headers = ["Fecha", "Número", "Clave de acceso", "Autorización", "RUC emisor", "Emisor", "Identificación receptor", "Receptor", "Subtotal", "Impuestos", "Total"];
            for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
            for (var i = 0; i < extra.Length; i++) sheet.Cell(1, 12 + i).Value = extra[i].Item1;
            var row = 2;
            foreach (var doc in group)
            {
                string? Value(string section, string tag) => doc.XmlData!.Fields.FirstOrDefault(f => f.Path.Contains("/" + section + "[1]/", StringComparison.Ordinal) && f.Path.EndsWith("/" + tag + "[1]", StringComparison.Ordinal))?.Value;
                sheet.Cell(row, 1).Value = doc.Issued.ToDateTime(TimeOnly.MinValue);
                sheet.Cell(row, 2).Value = doc.Number; sheet.Cell(row, 3).Value = doc.AccessKey; sheet.Cell(row, 4).Value = doc.Authorization;
                sheet.Cell(row, 5).Value = doc.IssuerTaxId; sheet.Cell(row, 6).Value = Value("infoTributaria", "razonSocial");
                sheet.Cell(row, 7).Value = doc.Identification; sheet.Cell(row, 8).Value = doc.Recipient;
                if (doc.Subtotal.HasValue) sheet.Cell(row, 9).Value = doc.Subtotal.Value;
                if (doc.Taxes.HasValue) sheet.Cell(row, 10).Value = doc.Taxes.Value;
                if (doc.Total.HasValue) sheet.Cell(row, 11).Value = doc.Total.Value;
                for (var i = 0; i < extra.Length; i++)
                {
                    var value = Value(info, extra[i].Item2);
                    if (extra[i].Item2.StartsWith("fecha", StringComparison.Ordinal) && DateOnly.TryParseExact(value, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    { sheet.Cell(row, 12 + i).Value = date.ToDateTime(TimeOnly.MinValue); sheet.Cell(row, 12 + i).Style.NumberFormat.Format = "dd/MM/yyyy"; }
                    else if (extra[i].Item2 == "totalDescuento" && decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
                    { sheet.Cell(row, 12 + i).Value = amount; sheet.Cell(row, 12 + i).Style.NumberFormat.Format = "#,##0.00"; }
                    else sheet.Cell(row, 12 + i).Value = value;
                }
                row++;
            }
            sheet.Column(1).Style.NumberFormat.Format = "dd/MM/yyyy";
            sheet.Columns(9, 11).Style.NumberFormat.Format = "#,##0.00";
        }
    }

    private static void AddItemSheets(XLWorkbook book, List<Voucher> documents)
    {
        foreach (var group in documents.Where(d => d.XmlData?.Items.Count > 0).GroupBy(d => d.Type))
        {
            var name = group.Key switch
            {
                "Facturas" => "Detalles facturas", "Retenciones" => "Detalles retenciones",
                "Notas-Credito" => "Detalles notas crédito", "Notas-Debito" => "Detalles notas débito",
                "Guias-Remision" => "Detalles guías", "Liquidaciones" => "Detalles liquidaciones", _ => "Detalles otros"
            };
            var sheet = book.AddWorksheet(name);
            string[] headers = ["Clave de acceso", "Número", "Sección", "Ruta XML", "Código", "Código auxiliar", "Descripción", "Cantidad", "Precio unitario", "Descuento", "Valor sin impuestos", "Base imponible", "Tarifa", "Valor impuesto", "Forma de pago", "Valor pago", "Identificación destinatario", "Destinatario", "Dirección destino", "Motivo traslado", "Valor motivo", "Tipo documento sustento", "Documento sustento", "Autorización sustento", "Fecha documento sustento"];
            for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
            var row = 2;
            foreach (var doc in group)
                foreach (var item in doc.XmlData!.Items)
                {
                    string? V(params string[] names) => names.Select(n => item.Values.FirstOrDefault(f => f.Path == n)?.Value).FirstOrDefault(v => v is not null);
                    void Number(int column, params string[] names)
                    {
                        if (decimal.TryParse(V(names), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) sheet.Cell(row, column).Value = n;
                    }
                    sheet.Cell(row, 1).Value = doc.AccessKey; sheet.Cell(row, 2).Value = doc.Number;
                    sheet.Cell(row, 3).Value = item.Kind; sheet.Cell(row, 4).Value = item.Path;
                    sheet.Cell(row, 5).Value = V("codigoPrincipal", "codigoInterno", "codigo");
                    sheet.Cell(row, 6).Value = V("codigoAuxiliar", "codigoAdicional", "codigoPorcentaje");
                    sheet.Cell(row, 7).Value = V("descripcion", "razon");
                    Number(8, "cantidad"); Number(9, "precioUnitario"); Number(10, "descuento"); Number(11, "precioTotalSinImpuesto");
                    Number(12, "baseImponible"); Number(13, "tarifa");
                    if (item.Kind is "impuesto" or "totalImpuesto") Number(14, "valor");
                    if (item.Kind == "pago") { sheet.Cell(row, 15).Value = V("formaPago"); Number(16, "total"); }
                    sheet.Cell(row, 17).Value = V("identificacionDestinatario"); sheet.Cell(row, 18).Value = V("razonSocialDestinatario");
                    sheet.Cell(row, 19).Value = V("dirDestinatario"); sheet.Cell(row, 20).Value = V("motivoTraslado");
                    if (item.Kind == "motivo") Number(21, "valor");
                    sheet.Cell(row, 22).Value = V("codDocSustento"); sheet.Cell(row, 23).Value = V("numDocSustento");
                    sheet.Cell(row, 24).Value = V("numAutDocSustento");
                    var supportDate = V("fechaEmisionDocSustento");
                    if (DateOnly.TryParseExact(supportDate, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    { sheet.Cell(row, 25).Value = parsedDate.ToDateTime(TimeOnly.MinValue); sheet.Cell(row, 25).Style.NumberFormat.Format = "dd/MM/yyyy"; }
                    row++;
                }
            sheet.Columns(8, 14).Style.NumberFormat.Format = "#,##0.00####";
            sheet.Column(16).Style.NumberFormat.Format = "#,##0.00####";
            sheet.Column(21).Style.NumberFormat.Format = "#,##0.00####";
        }
    }
}
