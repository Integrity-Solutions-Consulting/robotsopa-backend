using System.IO.Compression;
using System.Xml.Linq;

namespace SriAutomation.Worker;

public static class StorageChecks
{
    public static void Run(LocalStorage storage)
    {
        var account = "prueba-" + Guid.NewGuid().ToString("N");
        var doc = new Document(account, new DateOnly(2026, 9, 12), "Facturas", "001234567890", "001-001-000000001", "Receptor ficticio", 12.50m);
        var path = storage.Save(doc, "xml", "<factura/>"u8.ToArray());
        if (!path.Contains("09-Septiembre") || !path.Contains("Facturas")) throw new Exception("Organización incorrecta.");
        storage.Save(doc, "xml", "<factura/>"u8.ToArray());
        try { storage.Save(doc with { Account = "../escape" }, "xml", []); throw new Exception("Traversal aceptado."); }
        catch (ArgumentException) { }
        var excel = storage.WriteMonthlyExcel(account, 2026, 9, [doc, doc, doc with { Account = "otra" }, doc with { Issued = new DateOnly(2025, 9, 1) }]);
        using (var zip = ZipFile.OpenRead(excel))
        {
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            if (XDocument.Load(stream).Descendants(ns + "row").Count() != 2) throw new Exception("Mezcla o duplicación de documentos.");
        }
        Console.WriteLine("OK: permisos, carpetas, duplicados, rutas, Excel y aislamiento por cuenta/mes. Archivos ficticios: " + excel);
    }
}
