using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
namespace SriAutomation.Api;
public static class PanelEndpoints
{
    public static void MapPanelEndpoints(this WebApplication app,RouteGroupBuilder api,IPanelDatabase database)
    {
        api.MapGet("/audit",(IStateRepository r,HttpContext c,int page=1)=>r.Read(s=>new {total=s.Audit.Count(a=>(!a.IsTest||c.Request.Headers["X-SRI-Test-Data"]=="1") && (c.Items["PanelRole"] as string=="admin" || a.User==(string)c.Items["PanelUser"]!)),items=s.Audit.Where(a=>(!a.IsTest||c.Request.Headers["X-SRI-Test-Data"]=="1") && (c.Items["PanelRole"] as string=="admin" || a.User==(string)c.Items["PanelUser"]!)).OrderByDescending(a=>a.At).Skip((Math.Max(page,1)-1)*50).Take(50).ToList()}));
        api.MapGet("/settings",(IStateRepository r,HttpContext c)=>c.Items["PanelRole"] as string=="asistente" ? database.Preferences((string)c.Items["PanelUser"]!) : r.Read(s=>s.Settings));
        api.MapPut("/settings",(PanelSettings settings,IStateRepository r,HttpContext c)=>{ if(c.Items["PanelRole"] as string=="asistente") { database.SavePreferences((string)c.Items["PanelUser"]!,settings); return settings; } return r.Update(s=>{
            if(settings.DefaultAccount is {} account && !s.Accounts.Any(a=>a.Id==account))throw new ArgumentException("Cuenta predeterminada inválida.");
            if(!string.IsNullOrWhiteSpace(settings.DownloadSubfolder))Validation.Identifier(settings.DownloadSubfolder);
            if(s.Executions.Any(e=>e.Status is "Pending" or "Running"))throw new ArgumentException("Espere a que finalicen los trabajos antes de cambiar el destino.");
            s.Settings=settings;return settings;
        }); });
        api.MapPost("/accounts/register",(RegisterAccount request,IStateRepository r,HttpContext c)=>{
            if(string.IsNullOrWhiteSpace(request.Alias)||request.Alias.Length>150)throw new ArgumentException("Alias requerido.");
            if(request.TaxId is null||request.TaxId.Length!=13||!request.TaxId.All(char.IsAsciiDigit))throw new ArgumentException("RUC de 13 dígitos requerido.");
            var account=new Account("sri-"+Guid.NewGuid().ToString("N"),request.Alias.Trim(),request.TaxId,c.Request.Headers["X-SRI-Test-Data"]=="1");c.Items["AuditAccount"]=account.Id;
            return r.Update(s=>{if(!string.IsNullOrEmpty(request.Password))CredentialVault.Configured().Save(account.Id,request.TaxId,request.Password);s.Accounts.Add(account);return account;});
        });
        api.MapPost("/accounts/{id}/connect",(string id,IStateRepository r)=>r.Update(s=>AuthenticationFlow.Start(s,id)));
        api.MapGet("/accounts/{id}/credentials",(string id,IStateRepository r)=>r.Read(s=>{_ = s.Accounts.SingleOrDefault(a=>a.Id==id)??throw new KeyNotFoundException();var vault=CredentialVault.Configured();return new{configured=vault.Present(id),updatedAt=vault.Updated(id)};}));
        api.MapPut("/accounts/{id}/credentials",(string id,SaveCredential request,IStateRepository r)=>r.Update(s=>{var a=s.Accounts.SingleOrDefault(a=>a.Id==id)??throw new KeyNotFoundException();EnsureIdle(s,id);CredentialVault.Configured().Save(id,a.TaxId??a.Id,request.Password);return new{configured=true};}));
        api.MapDelete("/accounts/{id}/credentials",(string id,IStateRepository r)=>r.Update(s=>{_ = s.Accounts.SingleOrDefault(a=>a.Id==id)??throw new KeyNotFoundException();EnsureIdle(s,id);CredentialVault.Configured().Delete(id);return new{configured=false};}));
        api.MapPost("/executions/{id}/show-browser",(string id,IStateRepository r)=>r.Update(s=>{var op=s.Authentications.LastOrDefault(a=>a.ExecutionId==id&&a.BrowserOpened&&!a.CancelRequested)??throw new ArgumentException("No hay una ventana SRI disponible para esta operación.");op.Command="Show";return new{requested=true};}));
        api.MapPost("/executions/{id}/ready",(string id,IStateRepository r)=>r.Update(s=>{
            AuthenticationFlow.ReadyExecution(s,id);
            return new {verified=false,message="Comprobando el acceso real; Listo no confirma por sí solo la autenticación."};
        }));
    }
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record RegisterAccount(string Alias,string TaxId,string? Password=null){public override string ToString()=>"Registro [protegido]";}
    public sealed record SaveCredential(string Password){public override string ToString()=>"Credencial [protegida]";}
    private static void EnsureIdle(State s,string id){if(s.Executions.Any(e=>e.Query.Account==id&&e.Status is "Pending" or "Running")||s.Authentications.Any(a=>a.Account==id&&AuthenticationFlow.Active(a)))throw new ArgumentException("Cierre o cancele las operaciones de la cuenta antes de modificar su contraseña.");}
}
