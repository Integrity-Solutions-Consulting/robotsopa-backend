using ClosedXML.Excel;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class ReportDateTests
{
    [Fact]
    public void ReportWritesCalendarDatesAndLeavesInvalidDatesEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "sopa-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inputs = new string?[] { "2026-11-06", "06/11/2026", "2026-11-06T23:30:00-05:00", null, "", "invalid", "06/11/2026 10:20:30", "2026-02-30" };
            var documents = inputs.Select((value, i) => new Voucher { Account = "ruc1", Type = "Facturas", AccessKey = (i + 1).ToString(), Number = (i + 1).ToString(), Issued = new DateOnly(2026, 11, 6), AuthorizedAt = value }).ToArray();
            var storage = new DocumentStorage(root);
            using var book = new XLWorkbook(storage.Resolve(storage.Report("ruc1", 2026, 11, documents)));
            var sheet = book.Worksheet("Documentos");
            for (var i = 0; i < inputs.Length; i++)
            {
                var cell = sheet.Cell(i + 2, 4);
                if (i is 0 or 1 or 2 or 6)
                {
                    Assert.Equal(XLDataType.DateTime, cell.DataType);
                    Assert.Equal(new DateTime(2026, 11, 6), cell.GetDateTime());
                    Assert.Equal("dd/MM/yyyy", cell.Style.NumberFormat.Format);
                    Assert.Equal("06/11/2026", cell.GetFormattedString(System.Globalization.CultureInfo.GetCultureInfo("es-EC")));
                }
                else Assert.True(cell.IsEmpty());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
