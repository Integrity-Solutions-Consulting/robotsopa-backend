using ClosedXML.Excel;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class MonthlyReportContractTests
{
    [Fact]
    public void ReportContainsOnlyRequestedAccountAndMonthAndDeduplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sri-monthly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new DocumentStorage(directory);
            var selected = new Voucher { Account = "ruc1", Type = "Facturas", AccessKey = "123", Number = "001", Issued = new DateOnly(2025, 2, 1), AuthorizedAt = "2025-02-02T12:00:00-05:00", Total = 12.5m };
            var path = storage.Report("ruc1", 2025, 2, [selected, selected, new Voucher { Account = "ruc1", Type = "Facturas", AccessKey = "456", Issued = new DateOnly(2025, 3, 1) }, new Voucher { Account = "ruc2", Type = "Facturas", AccessKey = "789", Issued = new DateOnly(2025, 2, 1) }]);
            Assert.EndsWith(Path.Combine("ruc1", "2025", "02-Febrero", "Resumen_2025_02.xlsx"), path);
            using var workbook = new XLWorkbook(storage.Resolve(path));
            var sheet = workbook.Worksheet("Documentos");
            Assert.Equal(2, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal("Fecha de autorización", sheet.Cell(1, 4).GetString());
            Assert.Equal(new DateTime(2025, 2, 2), sheet.Cell(2, 4).GetDateTime());
            Assert.Equal("dd/MM/yyyy", sheet.Cell(2, 4).Style.NumberFormat.Format);
            Assert.Equal(12.5m, sheet.Cell(2, 8).GetValue<decimal>());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
