using ClosedXML.Excel;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;
public class WindowsDesktopStorageTests
{
    [Fact]
    public void SavesDeduplicatesAndRegeneratesMonthlyReport()
    {
        var desktop = Path.Combine(Path.GetTempPath(), "sri-desktop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(desktop);
        try {
            var d = new Voucher { Source = "Sri", Type = "Facturas", AccessKey = new string('1', 49), Issued = new DateOnly(2026, 3, 1), Number="001-001-1", Authorization="auth", AuthorizedAt="2026-03-01T12:00:00", Recipient="Empresa", Identification="1790000000001", Subtotal=10, Total=12 };
            byte[] content = [1, 2, 3]; var hash = DownloadPipeline.Hash(content);
            var store = new WindowsDesktopStorage(desktop);
            store.Save("1790000000001", d, "pdf", content, hash);
            new WindowsDesktopStorage(desktop).Save("1790000000001", d, "pdf", content, hash);
            var month = Path.Combine(desktop, "SRI-Automation", "1790000000001", "2026", "03");
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(month, "Facturas", d.AccessKey + ".pdf")));
            using var book = new XLWorkbook(Path.Combine(month, "Resumen_2026_03.xlsx"));
            var sheet=book.Worksheet(1); Assert.Equal(2, sheet.LastRowUsed()!.RowNumber()); Assert.Equal("auth", sheet.Cell(2,3).GetString()); Assert.Equal(12, sheet.Cell(2,8).GetDouble());
            Assert.Throws<IOException>(() => store.Save("1790000000001", d, "pdf", [9], DownloadPipeline.Hash([9])));
            Assert.Throws<InvalidDataException>(() => store.Save("1790000000001", d, "pdf", content, new string('0',64)));
            d.Source="Imported"; Assert.Throws<ArgumentException>(() => store.Save("1790000000001", d, "pdf", content, hash));
        } finally { Directory.Delete(desktop, true); }
    }
}
