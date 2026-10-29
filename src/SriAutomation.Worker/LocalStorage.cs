using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace SriAutomation.Worker;

public sealed record Document(string Account, DateOnly Issued, string Type, string AccessKey,
    string Number, string Recipient, decimal Total);

public sealed class LocalStorage
{
    private readonly string root;
    private static readonly string[] Months = ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"];
    private static readonly HashSet<string> Types = ["Facturas", "Retenciones", "Notas-Credito", "Notas-Debito", "Liquidaciones", "Guias-Remision"];
    public LocalStorage(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("El destino debe ser absoluto.");
        root = Path.GetFullPath(path);
    }
    private static string Safe(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Identificador de ruta inválido.");
        return value;
    }
    public string MonthDirectory(string account, DateOnly issued) => Path.Combine(root,
        Safe(account), issued.Year.ToString("D4"), $"{issued.Month:D2}-{Months[issued.Month - 1]}");
    public void CheckAccess()
    {
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "storage-check");
            if (File.ReadAllText(probe) != "storage-check") throw new IOException("Error de lectura.");
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }
    public string Save(Document document, string extension, byte[] content)
    {
        if (!Types.Contains(document.Type)) throw new ArgumentException("Tipo de comprobante inválido.");
        if (extension is not "xml" and not "pdf") throw new ArgumentException("Extensión inválida.");
        var directory = Path.Combine(MonthDirectory(document.Account, document.Issued), document.Type);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{Safe(document.AccessKey)}.{extension}");
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target))
            {
                if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                    throw new IOException("La clave ya existe con contenido diferente.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }
    public string WriteMonthlyExcel(string account, int year, int month, IEnumerable<Document> documents)
    {
        var directory = MonthDirectory(account, new DateOnly(year, month, 1));
        Directory.CreateDirectory(directory);
        // Un bloqueo de archivo también coordina procesos distintos que compartan el volumen.
        using var gate = new FileStream(Path.Combine(directory, ".excel.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var rows = documents.Where(d => d.Account == account && d.Issued.Year == year && d.Issued.Month == month)
            .DistinctBy(d => (d.Type, d.AccessKey)).OrderBy(d => d.Issued).ThenBy(d => d.AccessKey).ToList();
        var target = Path.Combine(directory, $"Resumen_{year:D4}_{month:D2}.xlsx");
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                Add(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Add(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Documentos\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XElement Row(IEnumerable<string> values) => new(ns + "row", values.Select(v => new XElement(ns + "c", new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", v)))));
                var data = new XElement(ns + "sheetData", Row(["Tipo", "Número", "Fecha de emisión", "Receptor", "Total", "Clave de acceso"]));
                foreach (var d in rows) data.Add(Row([d.Type, d.Number, d.Issued.ToString("yyyy-MM-dd"), d.Recipient, d.Total.ToString(CultureInfo.InvariantCulture), d.AccessKey]));
                Add(zip, "xl/worksheets/sheet1.xml", new XElement(ns + "worksheet", data).ToString());
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }
    private static void Add(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}
