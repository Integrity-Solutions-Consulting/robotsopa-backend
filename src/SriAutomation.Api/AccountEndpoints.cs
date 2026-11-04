using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

public static class AccountEndpoints
{
    private static string ProfilePath => Environment.GetEnvironmentVariable("SRI_PORTAL_PROFILE") ?? Path.GetFullPath("config/sri-portal.json");
    private static string SessionPath(string id) => Path.Combine(Environment.GetEnvironmentVariable("SRI_SESSION_PATH") ?? Path.GetFullPath("storage/sessions"), Validation.Identifier(id) + ".session");
    public static void MapAccountEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        api.MapPut("/accounts/{id}", (string id, Rename rename, IStateRepository r) => r.Update(s => {
            if (string.IsNullOrWhiteSpace(rename.Name) || rename.Name.Length > 150) throw new ArgumentException("Alias inválido.");
            var index = s.Accounts.FindIndex(a => a.Id == id); if (index < 0) throw new KeyNotFoundException();
            return s.Accounts[index] = s.Accounts[index] with { Name = rename.Name.Trim() };
        }));
        api.MapDelete("/accounts/{id}", (string id, IStateRepository r,HttpContext c) => r.Update(s => {
            Validation.Identifier(id);
            if (s.Authentications.Any(a => a.Account == id && AuthenticationFlow.Active(a)) || s.Executions.Any(e => e.Query.Account == id && e.Status is "Pending" or "Running")) throw new ArgumentException("Cancele primero las operaciones activas.");
            var removed=s.Accounts.SingleOrDefault(a=>a.Id==id);c.Items["AuditAccountName"]=removed?.Name;c.Items["AuditIsTest"]=removed?.IsTest==true;
            if (File.Exists(SessionPath(id))) File.Delete(SessionPath(id));
            CredentialVault.Configured().Delete(id);
            if (s.Accounts.RemoveAll(a => a.Id == id) == 0) throw new KeyNotFoundException();
            s.Schedules.RemoveAll(a => a.Query.Account == id); s.Configurations.RemoveAll(a => a.Account == id);
            s.Authentications.RemoveAll(a => a.Account == id);
            if(s.Settings.DefaultAccount==id)s.Settings.DefaultAccount=null;
            return new { removed = true, message = "Configuración eliminada. Documentos e historial conservados." };
        }));
        api.MapGet("/portal/capabilities", () => {
            var profile = JsonNode.Parse(File.ReadAllText(ProfilePath));
            return new { remoteLogout = profile?["verified"]?.GetValue<bool>() == true && !string.IsNullOrWhiteSpace(profile?["logout"]?.GetValue<string>()), realDownloadValidation = "Pending" };
        });
        api.MapGet("/accounts/{id}/session", (string id, IStateRepository r) => r.Read(s => {
            if (!s.Accounts.Any(a => a.Id == id)) throw new KeyNotFoundException();
            var op = s.Authentications.LastOrDefault(a => a.Account == id);
            var present = File.Exists(SessionPath(id));
            var valid = present && op?.SessionVerified == true && op.Expires > DateTimeOffset.UtcNow;
            return new { present, verified = valid, status = valid ? "Authenticated" : op?.Status == "Authenticated" ? "Expired" : op?.Status ?? "NotConfigured", operation = op,
                lastConnection = s.Authentications.LastOrDefault(a => a.Account == id && a.VerifiedAt.HasValue)?.VerifiedAt,
                lastQuery = s.Executions.LastOrDefault(e => e.Query.Account == id)?.Started,
                message = valid ? "Acceso verificado para esta operación." : op?.Status=="Authenticated"?"El acceso terminó. La siguiente consulta abrirá un inicio de sesión nuevo.":op?.Message ?? "El Worker usara la credencial cifrada al iniciar una consulta." };
        }));
        api.MapPost("/accounts/{id}/logout", (string id, IStateRepository r) => r.Update(s => {
            if (!s.Accounts.Any(a => a.Id == id)) throw new KeyNotFoundException();
            if (s.Executions.Any(e => e.Query.Account == id && e.Status is "Pending" or "Running")) throw new ArgumentException("Cancele la consulta activa antes de cerrar la sesión.");
            if (File.Exists(SessionPath(id))) File.Delete(SessionPath(id));
            var op = s.Authentications.LastOrDefault(a => a.Account == id);
            if (op is not null) { op.Command = "";op.CancelRequested=true; op.SessionVerified = false; op.Expires = null; op.Status = "Cancelled"; op.Message = "Sesión local revocada; cierre remoto no confirmado."; }
            return new { status = "Cancelled", message = "Sesión local revocada. No se afirma un cierre remoto del SRI." };
        }));
        api.MapGet("/portal/profile", () => Results.Text(File.ReadAllText(ProfilePath), "application/json"));
        api.MapGet("/portal/controls", (IStateRepository r) => r.Read(s => s.PortalControls));
        api.MapPut("/portal/profile", (JsonElement draft, IStateRepository r) => {
            if (r.Read(s => s.Executions.Any(e => e.Status is "Running" or "Pending") || s.Authentications.Any(a => a.Status is "OpeningBrowser" or "InspectingPortal"))) throw new ArgumentException("Espere a que termine la operación activa.");
            var text = draft.GetRawText(); if (text.Length > 32000) throw new ArgumentException("Perfil demasiado grande.");
            var node = JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("Perfil inválido.");
            if (node["url"]?.GetValue<string>() != "https://srienlinea.sri.gob.ec/comprobantes-electronicos-internet/pages/consultas/menu.jsf") throw new ArgumentException("Solo se admite el portal oficial.");
            node["verified"] = false; node["verifiedAt"] = null; node["maxDocuments"] = 10; node["maxAutomaticDays"] = 1;
            var temp = ProfilePath + ".tmp"; File.WriteAllText(temp, node.ToJsonString(new() { WriteIndented = true })); File.Move(temp, ProfilePath, true);
            return new { status = "NotConfigured", message = "Borrador guardado; requiere inspección y verificación desde el navegador autorizado." };
        });
        api.MapPost("/connection-tests", (Query query, IStateRepository r) => r.Update(s => {
            Validation.Query(query, s);
            if (query.Mode != "Sri" || query.From != query.To || query.Types.Length != 1 || query.BroadRangeApproved) throw new ArgumentException("La prueba admite solo un día, un tipo y hasta 10 documentos.");
            if (!File.Exists(ProfilePath) || JsonNode.Parse(File.ReadAllText(ProfilePath))?["verified"]?.GetValue<bool>() != true) throw new ArgumentException("Consulta no disponible: selectores del SRI pendientes de inspección y validación real.");
            return Validation.Enqueue(s, query);
        }));
    }
    public sealed record Rename(string Name);
}
