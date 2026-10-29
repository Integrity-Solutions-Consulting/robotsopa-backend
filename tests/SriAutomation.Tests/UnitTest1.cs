namespace SriAutomation.Tests;

public class StorageFailureTests
{
    [Fact]
    public void DestinationThatIsAFileCannotBeUsedAsStorage()
    {
        var file = Path.Combine(Path.GetTempPath(), "sri-error-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try { Assert.Throws<IOException>(() => new SriAutomation.Infrastructure.DocumentStorage(file).CheckAccess()); }
        finally { File.Delete(file); }
    }
    [Fact]
    public void PdfIsReadAndComparedWithXmlKey()
    {
        var key = "0109202601179000000000110010010000000011234567811";
        Assert.Null(new SriAutomation.Infrastructure.PdfInspector().Check(PdfFixture.Build(key), key));
        Assert.NotNull(new SriAutomation.Infrastructure.PdfInspector().Check(PdfFixture.Build("99999999"), key));
    }
}
