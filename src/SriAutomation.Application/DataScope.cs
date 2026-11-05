using SriAutomation.Domain;
namespace SriAutomation.Application;

public static class DataScope
{
    public static void MarkLegacyFixtures(State state)
    {
        for (var i = 0; i < state.Accounts.Count; i++)
        {
            var a = state.Accounts[i];
            if (a.Id.StartsWith("qa-", StringComparison.Ordinal) && a.Name.StartsWith("QA FICTICIA", StringComparison.Ordinal))
                state.Accounts[i] = a with { IsTest = true };
        }
    }
    public static bool Visible(State state, string account, bool includeTests) =>
        includeTests || !state.Accounts.Any(a => a.Id == account && a.IsTest);
}
