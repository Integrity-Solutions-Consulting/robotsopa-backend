using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public class ExecutionBlockingTests
{
    private static readonly Query ServerQuery = new("a", new(2025, 1, 1), new(2025, 1, 1), ["Facturas"]);

    [Fact]
    public void HistoricalHumanActionExecutionsDoNotBlockServerRequest()
    {
        var state = new State { Accounts = [new("a", "Cuenta")] };
        foreach (var mode in new[] { "LOCAL_WINDOWS", "LEGACY" })
            state.Executions.Add(new Execution { Query = ServerQuery with { StorageMode = mode }, Status = "RequiresHumanAction" });

        var created = Validation.Enqueue(state, ServerQuery);

        Assert.Equal("Pending", created.Status);
        Assert.Equal(2, state.Executions.Count(e => e.Status == "RequiresHumanAction"));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    public void ActiveExecutionReportsActualBlocker(string status)
    {
        var state = new State { Accounts = [new("a", "Cuenta")] };
        state.Executions.Add(new Execution { Id = "blocking-id", Query = ServerQuery, Status = status });

        var error = Assert.Throws<ArgumentException>(() => Validation.Enqueue(state, ServerQuery));

        Assert.Contains("blocking-id", error.Message);
        Assert.Contains(status, error.Message);
        Assert.Single(state.Executions);
    }
}
