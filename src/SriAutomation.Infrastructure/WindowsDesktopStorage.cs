using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>Writes a validated SRI document to an explicitly supplied Windows desktop root.</summary>
public sealed class WindowsDesktopStorage
{
    private readonly string root;
    public WindowsDesktopStorage(string desktop)
    {
        if (!Path.IsPathFullyQualified(desktop)) throw new ArgumentException("El escritorio debe ser una ruta absoluta.");
        root = Path.Combine(Path.GetFullPath(desktop), "SRI-Automation");
    }
    public static WindowsDesktopStorage ForCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Se requiere Windows.");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(desktop)) throw new IOException("Windows no proporcionó la ruta del escritorio.");
        return new WindowsDesktopStorage(desktop);
    }
    public void Save(string taxId, Voucher document, string extension, byte[] bytes, string sha256)
    {
        if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new ArgumentException("RUC inválido.");
        if (document.Source != "Sri" || !Validation.Types.Contains(document.Type) || extension is not ("xml" or "pdf") || document.AccessKey.Length != 49 || !document.AccessKey.All(char.IsAsciiDigit)) throw new ArgumentException("Comprobante inválido.");
        if (bytes.Length == 0 || bytes.Length > 20_000_000 || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Huella de archivo inválida.");
        var month = Path.Combine(root, taxId, document.Issued.Year.ToString("D4"), document.Issued.Month.ToString("D2"));
        Directory.CreateDirectory(month);
        using var gate = new FileStream(Path.Combine(month, ".resumen.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var directory = Path.Combine(month, document.Type); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, document.AccessKey + "." + extension);
        AtomicCreate(target, bytes);
        var ledger = Path.Combine(month, ".resumen.json");
        var rows = File.Exists(ledger) ? JsonSerializer.Deserialize<List<Voucher>>(File.ReadAllBytes(ledger)) ?? throw new InvalidDataException("Índice mensual inválido.") : [];
        rows.RemoveAll(d => d.Type == document.Type && d.AccessKey == document.AccessKey);
        rows.Add(document);
        AtomicReplace(ledger, JsonSerializer.SerializeToUtf8Bytes(rows));
        WriteReport(month, document.Issued, rows);
    }
    private static void AtomicCreate(string target, byte[] bytes)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            try { File.Move(temp, target, false); }
            catch (IOException) when (File.Exists(target)) { if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) throw new IOException("Ya existe un archivo diferente para esta clave."); }
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void AtomicReplace(string target, byte[] bytes)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); } File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void WriteReport(string month, DateOnly date, List<Voucher> rows)
    {
        using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Comprobantes");
        string[] headers = ["Tipo", "Número de comprobante", "Autorización", "Fecha y hora de autorización", "Razón social", "Identificación", "Subtotal", "Total"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var d in rows.OrderBy(d => d.Issued).ThenBy(d => d.AccessKey)) {
            sheet.Cell(row, 1).Value = d.Type; sheet.Cell(row, 2).Value = d.Number ?? ""; sheet.Cell(row, 3).Value = d.Authorization ?? "";
            sheet.Cell(row, 4).Value = d.AuthorizedAt ?? ""; sheet.Cell(row, 5).Value = d.Recipient ?? ""; sheet.Cell(row, 6).Value = d.Identification ?? "";
            if (d.Subtotal is {} subtotal) sheet.Cell(row, 7).Value = subtotal;
            if (d.Total is {} total) sheet.Cell(row, 8).Value = total;
            row++;
        }
        sheet.Row(1).Style.Font.Bold = true; sheet.Columns().AdjustToContents();
        using var buffer = new MemoryStream(); book.SaveAs(buffer);
        AtomicReplace(Path.Combine(month, $"Resumen_{date.Year:D4}_{date.Month:D2}.xlsx"), buffer.ToArray());
    }
}
