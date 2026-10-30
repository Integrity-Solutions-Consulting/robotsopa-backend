using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

// Deny unrecognized assistant operations by default. Every account-bound route
// is checked on the server, even when an identifier is supplied directly.
public static class AssistantAccess
{
    public static async Task<bool> Allowed(HttpContext c, IPanelDatabase db, IStateRepository repository)
    {
        var parts=c.Request.Path.Value!.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length<3 || parts[0]!="api" || parts[1]!="v1") return false;
        var p=parts.Skip(2).ToArray(); var method=c.Request.Method; var user=(string)c.Items["PanelUser"]!;
        var assigned=db.AssignedAccounts(user);
        bool Has(string? account)=>account is not null && assigned.Contains(account);
        if(p.Length==2 && p[0]=="auth" && (p[1]=="session" || p[1]=="logout" || p[1]=="change-password")) return true;
        if(p.Length==1 && method=="GET" && new[]{"accounts","audit","settings","system","dashboard","executions","documents","schedules","configurations"}.Contains(p[0])) return true;
        if(p.Length==2 && method=="GET" && (p[0]=="worker" && p[1]=="status" || p[0]=="agent" && p[1]=="status" || p[0]=="portal" && p[1]=="capabilities")) return true;
        if(p.Length==1 && p[0]=="settings" && method=="PUT") return true;
        if(p[0]=="accounts" && p.Length>=3) return Has(p[1]) && ((method=="GET" && p.Length==3 && p[2]=="session") || (method=="POST" && (p[2]=="connect" || p[2]=="authentication" || p[2]=="logout")));
        if(p[0]=="reports" && method=="GET" && p.Length==4) return Has(p[1]);
        if(p[0]=="documents" && p.Length>=3) {
            if(p[1] is "import" or "reindex") return method=="POST" && Has(p[2]);
            var account=repository.Read(s=>s.Documents.FirstOrDefault(d=>d.Id==p[1])?.Account); return Has(account) && (method=="GET" || method=="POST" && p[2]=="pdf");
        }
        if(p[0]=="executions" && p.Length>=2) {
            var account=repository.Read(s=>s.Executions.FirstOrDefault(e=>e.Id==p[1])?.Query.Account);
            return Has(account) && (method=="GET" && p.Length==2 || method=="POST" && p.Length==3 && new[]{"cancel","resume","retry","ready","show-browser"}.Contains(p[2]));
        }
        if(p[0]=="schedules" && p.Length>=2) {
            var account=repository.Read(s=>s.Schedules.FirstOrDefault(e=>e.Id==p[1])?.Query.Account);
            if(!Has(account)) return false;
            if(method=="DELETE" && p.Length==2 || method=="POST" && p.Length==3 && p[2]=="run") return true;
            if(method!="PUT" || p.Length!=2) return false;
        }
        else if(!(p.Length==1 && method=="POST" && new[]{"executions","comprobantes","reports","schedules","configurations","connection-tests"}.Contains(p[0]))) return false;
        try {
            c.Request.EnableBuffering(); using var json=await JsonDocument.ParseAsync(c.Request.Body); c.Request.Body.Position=0;
            var body=json.RootElement;
            if(p[0]=="schedules") { if(!body.TryGetProperty("query",out body)) return false; }
            return body.TryGetProperty("account",out var account) && account.ValueKind==JsonValueKind.String && Has(account.GetString());
        } catch(JsonException) { c.Request.Body.Position=0; return false; }
    }
}
