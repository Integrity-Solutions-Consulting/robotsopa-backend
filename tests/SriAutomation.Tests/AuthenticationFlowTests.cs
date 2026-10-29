using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class AuthenticationFlowTests
{
    private static State Ready() => new() { AgentHeartbeat = DateTimeOffset.UtcNow, Accounts = [new("autorizada", "Cuenta autorizada", "1790000000001")] };
    [Fact]
    public void OfflineAgentCannotClaimBrowserOpened()
    {
        var state = Ready(); state.AgentHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Throws<ArgumentException>(() => AuthenticationFlow.Start(state, "autorizada"));
        Assert.Empty(state.Authentications);
    }
    [Fact]
    public void StartRequiresSeparateManualVerificationAndCancellationRevokesReadiness()
    {
        var state = Ready(); var op = AuthenticationFlow.Start(state, "autorizada");
        Assert.Equal("Ready", op.Status); Assert.False(op.BrowserOpened); Assert.False(op.SessionVerified);
        Assert.Throws<ArgumentException>(() => AuthenticationFlow.Start(state, "autorizada"));
        op.Status = "WaitingForLogin"; AuthenticationFlow.Command(state, "autorizada", "Verify");
        Assert.Equal("Verify", op.Command); Assert.False(op.SessionVerified);
        AuthenticationFlow.Command(state, "autorizada", "Cancel");
        Assert.True(op.CancelRequested); Assert.False(op.SessionVerified); Assert.Null(op.Expires);
    }
    [Fact]
    public void ExistingAccountCanUseItsRucAsIdentifierWithoutDuplicatingTheField()
    {
        var state = new State { AgentHeartbeat = DateTimeOffset.UtcNow, Accounts = [new("1790000000001", "Cuenta con RUC como identificador")] };
        var operation = AuthenticationFlow.Start(state,"1790000000001");
        Assert.Equal("Ready",operation.Status); Assert.False(operation.SessionVerified);
    }
    [Fact]
    public void TestAccountsAndActiveQueriesCannotOpenRealPortal()
    {
        var state = Ready(); state.Accounts.Add(new("qa", "QA", "1790000000001", true));
        Assert.Throws<ArgumentException>(() => AuthenticationFlow.Start(state, "qa"));
        Validation.Enqueue(state, new("autorizada", new(2026, 10, 12), new(2026, 10, 12), ["Facturas"]));
        Assert.Throws<ArgumentException>(() => AuthenticationFlow.Start(state, "autorizada"));
    }
    [Fact]
    public void QueryAllowsOneYearAndUsesPdfWithoutDocumentLimit()
    {
        var query = new Query("autorizada", new(2025, 10, 13), new(2026, 10, 12), ["Facturas"], MaxDocuments: 0);
        var job = Validation.Enqueue(Ready(), query);
        Assert.True(job.Query.Pdf); Assert.False(job.Query.Xml);
        Assert.Throws<ArgumentException>(() => Validation.Enqueue(Ready(), query with { From = new(2025, 10, 12) }));
    }
    [Fact]
    public void AuthenticationCommandsAndLimitsSurviveRepositoryRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sri-auth-"+Guid.NewGuid().ToString("N"));
        try {
            var first = new JsonStateRepository(folder);
            first.Update(s => { s.Accounts.Add(new("a", "Persistencia", "1790000000001")); s.AgentHeartbeat=DateTimeOffset.UtcNow; return AuthenticationFlow.Start(s,"a"); });
            var second = new JsonStateRepository(folder);
            Assert.Equal("Open",second.Read(s=>s.Authentications.Single().Command));
            second.Update(s=>AuthenticationFlow.Command(s,"a","Cancel"));
            Assert.True(new JsonStateRepository(folder).Read(s=>s.Authentications.Single().CancelRequested));
        } finally { if (Directory.Exists(folder)) Directory.Delete(folder,true); }
    }
}
