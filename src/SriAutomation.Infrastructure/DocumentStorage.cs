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
        CleanupStaging();
        var probe = Path.Combine(root, ".probe-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "ok"); if (File.ReadAllText(probe) != "ok") throw new IOException("Sin acceso de lectura."); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }
    private void CleanupStaging()
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
            {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        var preserveTemporary = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(content); file.Flush(true); }
            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path))
            { if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) throw new ArgumentException("Ya existe contenido distinto para esta clave."); }
        }
        catch (IOException)
        {
            preserveTemporary = true;
            throw;
        }
        finally { if (!preserveTemporary && File.Exists(temporary)) File.Delete(temporary); }
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
        var rows = documents.Where(d => d.Account == account && d.Issued.Year == year && d.Issued.Month == month)
            .DistinctBy(d => (d.Type, d.AccessKey)).OrderBy(d => d.Issued).ThenBy(d => d.Number).ToList();
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Documentos");
        string[] headers = ["Tipo de comprobante", "Número de documento", "Número de autorización", "Fecha y hora de autorización", "Razón social", "Identificación", "Subtotal", "Total"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var d in rows)
        {
            sheet.Cell(row, 1).Value = d.Type; sheet.Cell(row, 2).Value = d.Number;
            sheet.Cell(row, 3).Value = d.Authorization;
            if (TryAuthorizationDate(d.AuthorizedAt, out var authorizationDate))
                sheet.Cell(row, 4).Value = authorizationDate.ToDateTime(TimeOnly.MinValue);
            else
                System.Diagnostics.Trace.TraceWarning("Fecha de autorizaci?n ausente o inv?lida para comprobante {0}.", d.AccessKey);
            sheet.Cell(row, 5).Value = d.Recipient; sheet.Cell(row, 6).Value = d.Identification;
            if (d.Subtotal.HasValue) sheet.Cell(row, 7).Value = d.Subtotal.Value;
            if (d.Total.HasValue) sheet.Cell(row, 8).Value = d.Total.Value;
            row++;
        }
        sheet.Columns(7, 8).Style.NumberFormat.Format = "#,##0.00";
        sheet.Column(4).Style.NumberFormat.Format = "dd/MM/yyyy";
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
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return relative;
    }
}
