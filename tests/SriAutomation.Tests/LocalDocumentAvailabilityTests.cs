using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;
public class LocalDocumentAvailabilityTests
{
    [Theory]
    [InlineData("pdf")]
    [InlineData("xml")]
    public void MissingExistingAndDeletedAreIndependent(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var bytes = new byte[] { 1, 2, 3, 4 };
        var hash = DownloadPipeline.Hash(bytes);
        var document = new Voucher { Account = "account", Type = "Facturas", AccessKey = new string('1', 49), Issued = new DateOnly(2026, 9, 12), PdfSha256 = hash, XmlSha256 = hash };
        var path = Path.Combine(root, "1234567890123", "2026", "09", "Facturas", document.AccessKey + "." + extension);
        try
        {
            Assert.False(LocalDocumentAvailability.IsAvailable(root, "1234567890123", document, extension));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            Assert.True(LocalDocumentAvailability.IsAvailable(root, "1234567890123", document, extension));
            Assert.False(LocalDocumentAvailability.IsAvailable(root, "1234567890123", document, extension == "pdf" ? "xml" : "pdf"));
            File.WriteAllBytes(path, [9]);
            Assert.False(LocalDocumentAvailability.IsAvailable(root, "1234567890123", document, extension));
            File.Delete(path);
            Assert.False(LocalDocumentAvailability.IsAvailable(root, "1234567890123", document, extension));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
