using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class SharePointDocumentPathTests
{
    [Fact]
    public void OrganizesByRucYearMonthAndType()
    {
        var document = new Voucher { Type = "Facturas", AccessKey = "123456", Issued = new DateOnly(2026, 9, 12) };
        Assert.Equal("Comprobantes/1790000000001/2026/09/Facturas/123456.xml", SharePointDocumentPath.Build("Comprobantes", "1790000000001", document, "xml"));
        Assert.Throws<ArgumentException>(() => SharePointDocumentPath.Build("../escape", "1790000000001", document, "xml"));
        Assert.Throws<ArgumentException>(() => SharePointDocumentPath.Build("Comprobantes", "not-a-ruc", document, "xml"));
        Assert.Throws<ArgumentException>(() => SharePointDocumentPath.Build("Comprobantes", "1790000000001", document, "exe"));
    }
}
