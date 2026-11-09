using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public sealed class SriRequestedFormatsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EnqueuePreservesUserFormats(bool pdf, bool xml)
    {
        var state = new State { Accounts = [new("synthetic", "FICTICIA", "1790000000001", true)] };
        var query = new Query("synthetic", new(2026, 10, 1), new(2026, 10, 1), ["Facturas"], "Sri", pdf, xml);
        var job = Validation.Enqueue(state, query);
        Assert.Equal(pdf, job.Query.Pdf); Assert.Equal(xml, job.Query.Xml);
    }
}
