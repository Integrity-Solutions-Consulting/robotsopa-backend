using SriAutomation.Domain;
namespace SriAutomation.Application;
public static class AuditTrail
{
    public static void Add(State state,string user,string action,string? account,string result,bool isTest=false) => state.Audit.Add(new(Guid.NewGuid().ToString("N"),user,action,DateTimeOffset.UtcNow,account,result,isTest||account is not null && state.Accounts.Any(a=>a.Id==account && a.IsTest),state.Accounts.FirstOrDefault(a=>a.Id==account)?.Name));
}
