using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

public static class AgentEndpoints
{
    public static bool Authorized(HttpContext context)
    {
        // Sin CORS ni cookies de usuario: solo el agente local conoce esta clave independiente.
        if (context.Request.Headers.ContainsKey("Origin")) return false;
        var file = Environment.GetEnvironmentVariable("SRI_AGENT_KEY_FILE");
        if (file is null || !File.Exists(file)) return false;
        var expected = File.ReadAllText(file).Trim();
        var supplied = context.Request.Headers["X-SRI-Agent-Key"].ToString();
        return expected.Length >= 64 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
    private static string ProfilePath => Environment.GetEnvironmentVariable("SRI_PORTAL_PROFILE") ?? Path.GetFullPath("config/sri-portal.json");
    private static string SessionPath(string id) => Path.Combine(Environment.GetEnvironmentVariable("SRI_SESSION_PATH") ?? Path.GetFullPath("storage/sessions"), Validation.Identifier(id) + ".session");
    public static void MapAgentEndpoints(this WebApplication app, RouteGroupBuilder api)
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
        api.MapGet("/agent/status", (IStateRepository r) => r.Read(s => new { online = AuthenticationFlow.AgentOnline(s), heartbeat = s.AgentHeartbeat }));
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
                agentOnline = AuthenticationFlow.AgentOnline(s), lastConnection = s.Authentications.LastOrDefault(a => a.Account == id && a.VerifiedAt.HasValue)?.VerifiedAt,
                lastQuery = s.Executions.LastOrDefault(e => e.Query.Account == id)?.Started,
                message = valid ? "Acceso verificado para esta operación." : op?.Status=="Authenticated"?"El acceso terminó. La siguiente consulta abrirá un inicio de sesión nuevo.":op?.Message ?? "Al solicitar una descarga se abrirá el portal oficial. Escriba su clave únicamente allí." };
        }));
        api.MapPost("/accounts/{id}/authentication", (string id, IStateRepository r) => r.Update(s => AuthenticationFlow.Start(s, id)));
        api.MapPost("/accounts/{id}/authentication/{command}", (string id, string command, IStateRepository r) => r.Update(s => AuthenticationFlow.Command(s, id, command)));
        api.MapPost("/accounts/{id}/logout", (string id, IStateRepository r) => r.Update(s => {
            if (!s.Accounts.Any(a => a.Id == id)) throw new KeyNotFoundException();
            if (s.Executions.Any(e => e.Query.Account == id && e.Status is "Pending" or "Running")) throw new ArgumentException("Cancele la consulta activa antes de cerrar la sesión.");
            if (File.Exists(SessionPath(id))) File.Delete(SessionPath(id));
            var op = s.Authentications.LastOrDefault(a => a.Account == id);
            if (op is not null) { op.Command = op.Owner=="Agent"?"Logout":"";op.CancelRequested=true; op.SessionVerified = false; op.Expires = null; op.Status = "Cancelled"; op.Message = "Sesión local revocada; cierre remoto pendiente de confirmación del agente."; }
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
            var op = s.Authentications.LastOrDefault(a => a.Account == query.Account);
            if (op?.SessionVerified != true || op.Expires <= DateTimeOffset.UtcNow || !File.Exists(SessionPath(query.Account))) throw new ArgumentException("Autentique y verifique la cuenta seleccionada antes de consultar.");
            return Validation.Enqueue(s, query);
        }));
        var agent = app.MapGroup("/api/internal/agent");
        agent.MapPost("/observations",(Observation report,IStateRepository r)=>r.Update(s=>{
            var op=s.Authentications.Single(a=>a.Id==report.Operation);var o=report.Value;
            if(!new[]{"LoginOpened","IdentityConfirmed","QueryOpening","QueryReady","QueryStopped"}.Contains(o.Phase)||!new[]{"Closed","Transition","BrowserError","OtherOrigin","SriLogin","SriAccount","SriQuery","SriOther"}.Contains(o.Location)||o.Tabs is <0 or >30||o.Frames is <0 or >100)throw new ArgumentException("Observación inválida.");
            op.Observations.Add(o with{At=DateTimeOffset.UtcNow});if(op.Observations.Count>20)op.Observations.RemoveAt(0);return new{saved=true};
        }));
        agent.MapPost("/recover", (IStateRepository r) => r.Update(s => {
            var accounts = new List<string>();
            foreach (var op in s.Authentications.Where(a=>a.Owner=="Agent"&&AuthenticationFlow.Active(a))) {
                op.Status = "Failed"; op.Command = ""; op.BrowserOpened = false; op.SessionVerified = false; op.Expires = null;
                op.Message = "Agente reiniciado. Inicie nuevamente la autenticación desde Angular; no se reabrió un navegador automáticamente.";
                if (File.Exists(SessionPath(op.Account))) File.Delete(SessionPath(op.Account));
                accounts.Add(op.Account);
            }
            s.AgentHeartbeat = DateTimeOffset.UtcNow; return accounts;
        }));
        agent.MapPost("/heartbeat", (IStateRepository r) => r.Update(s => { s.AgentHeartbeat = DateTimeOffset.UtcNow; foreach(var op in s.Authentications.Where(a=>a.Owner=="Agent"&&a.SessionVerified&&a.BrowserOpened&&!a.CancelRequested&&a.Expires>DateTimeOffset.UtcNow&&a.ExecutionId is not null&&s.Executions.Any(e=>e.Id==a.ExecutionId&&e.Status=="Running"&&!e.CancelRequested))) op.Expires=DateTimeOffset.UtcNow.AddMinutes(10); return new { online = true }; }));
        agent.MapPost("/controls", (List<PortalControl> controls, IStateRepository r) => r.Update(s => {
            if (controls.Count > 300 || controls.Any(c => !new[] { "INPUT", "SELECT", "BUTTON", "FORM", "TABLE" }.Contains(c.Tag) || c.Id is not null && (!System.Text.RegularExpressions.Regex.IsMatch(c.Id, @"^[a-zA-Z_][a-zA-Z0-9_:.-]{0,119}$") || System.Text.RegularExpressions.Regex.IsMatch(c.Id, @"\d{10}")))) throw new ArgumentException("Metadatos inválidos.");
            s.PortalControls = controls.Select(c => c with { Type = c.Type is "text" or "submit" or "button" or "select-one" or "select-multiple" or "checkbox" or "radio" ? c.Type : null }).ToList();
            return new { saved = true };
        }));
        agent.MapPost("/poll", (IStateRepository r) => {
            if (!r.Read(s => s.Authentications.Any(a => a.Owner == "Agent" && a.Command.Length > 0))) return null;
            return r.Update(s => {
            s.AgentHeartbeat = DateTimeOffset.UtcNow;
            foreach (var expired in s.Authentications.Where(a => a.Owner=="Agent" && a.Command == "Open" && a.Created < DateTimeOffset.UtcNow.AddMinutes(-5))) {
                expired.Command = ""; expired.Status = "Failed"; expired.Message = "Solicitud de apertura vencida. Inicie otra desde Angular.";
            }
            var op = s.Authentications.FirstOrDefault(a => a.Owner=="Agent" && a.Command.Length > 0);
            if (op is null) return null;
            var command = op.Command; op.Command = ""; op.Updated = DateTimeOffset.UtcNow;
            if (command == "Open") { op.Status = "OpeningBrowser"; op.Message = "Conexión manual en el navegador local; acceso aún sin confirmar."; }
            var account = s.Accounts.SingleOrDefault(a => a.Id == op.Account);
            return new { operation = op.Id, account = op.Account,executionId=op.ExecutionId, taxId = string.IsNullOrWhiteSpace(account?.TaxId) ? account?.Id : account.TaxId, command, cancelRequested = op.CancelRequested };
            });
        });
        agent.MapPost("/report", (AgentReport report, IStateRepository r) => r.Update(s => {
            s.AgentHeartbeat = DateTimeOffset.UtcNow;
            var op = s.Authentications.SingleOrDefault(a => a.Id == report.Operation) ?? throw new KeyNotFoundException();
            var messages = new Dictionary<string, string> {
                ["WaitingForLogin"] = "Abriendo el acceso autorizado. El bot utiliza la contraseña cifrada del perfil cuando está configurada; si el SRI solicita intervención, complétela en su ventana.",
                ["RequiresHumanAction"] = "Navegador disponible. Complete el acceso o revise el error del portal. Si los selectores aún no están inspeccionados, la sesión permanecerá pendiente.",
                ["InspectingPortal"] = "Conectando y comprobando automáticamente la identidad. No necesita pulsar Verificar.",
                ["PortalUnavailable"] = "El portal oficial no respondió en el navegador local. No se confirmó el acceso; no se ha confirmado una sesión.",
                ["PendingValidation"] = "El sistema no pudo confirmar automáticamente el acceso. La autenticación real está pendiente; no se ejecutaron consultas ni se ha almacenado una contraseña.",
                ["Authenticated"] = "Sesión y RUC verificados; sesión cifrada guardada. La descarga real aún debe comprobarse con una consulta autorizada.",
                ["Failed"] = "El agente no pudo completar la operación. Reintente desde Angular; no se confirmó autenticación.",
                ["VerificationFailed"] = "No se pudo confirmar todavía el acceso. Revise la ventana SRI y vuelva a pulsar Listo.",
                ["QueryCompleted"] = "Consultas SRI finalizadas. Generando y comprobando el Excel.",
                ["QueryFailed"] = "No se pudo completar la consulta del SRI. No se declaró éxito.",
                ["Cancelled"] = "Operación cancelada y navegador cerrado. Sesión local revocada; cierre remoto no confirmado." };
            if (!messages.TryGetValue(report.Status, out var message)) throw new ArgumentException("Estado de agente inválido.");
            if(report.ReasonCode is not null)message=AccessMessages.For(report.ReasonCode)??throw new ArgumentException("Motivo de comprobación inválido.");
            if ((op.CancelRequested || op.Status == "Cancelled") && report.Status != "Cancelled") return op;
            op.Status = report.Status; op.Message = message; op.BrowserOpened = report.BrowserOpened;
            op.VerificationCode=report.ReasonCode;
            if (report.Status == "Cancelled" && report.RemoteLogoutConfirmed) op.Message = "Cierre remoto confirmado en el portal y sesión local revocada.";
            if(report.Status=="QueryCompleted"&&op.ExecutionId is {} completedId&&s.Executions.Single(e=>e.Id==completedId).SriCheckpoint?.Complete!=true)throw new ArgumentException("El bot no confirmó la finalización de las consultas.");
            op.SessionVerified = report.Status == "Authenticated" && File.Exists(SessionPath(op.Account));
            if (report.Status == "Authenticated" && !op.SessionVerified) throw new ArgumentException("No existe una sesión cifrada.");
            op.Expires = op.SessionVerified ? DateTimeOffset.UtcNow.AddMinutes(10) : null; op.Updated = DateTimeOffset.UtcNow;
            if (op.SessionVerified) op.VerifiedAt = DateTimeOffset.UtcNow;
            if(op.ExecutionId is {} executionId&&s.Executions.SingleOrDefault(e=>e.Id==executionId) is {} job&&job.Status=="Running"&&!job.CancelRequested){
                ExecutionProgress.Set(s,job.Id,report.Status switch {"Authenticated"=>"PreparingQuery","InspectingPortal"=>"VerifyingLogin","OpeningBrowser"=>"OpeningBrowser","VerificationFailed"=>"AccessRejected","QueryCompleted"=>"Processing","QueryFailed"=>"QueryFailed",_=>"WaitingForLogin"},message);
            }
            return op;
        }));
    }
    public sealed record Rename(string Name);
    public sealed record Observation(string Operation,BrowserObservation Value);
    public sealed record AgentReport(string Operation, string Status, bool BrowserOpened, bool RemoteLogoutConfirmed = false,string? ReasonCode=null);
}


//coemntario