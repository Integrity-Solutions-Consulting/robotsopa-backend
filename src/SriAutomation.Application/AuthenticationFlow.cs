using SriAutomation.Domain;

namespace SriAutomation.Application;

public static class AuthenticationFlow
{
    public static void ReadyExecution(State state,string id)
    {
        var job=state.Executions.SingleOrDefault(e=>e.Id==id)??throw new KeyNotFoundException();
        var op=state.Authentications.LastOrDefault(a=>a.ExecutionId==id)??throw new ArgumentException("No hay una ventana SRI abierta para esta operación.");
        if(!job.CancelRequested&&job.Status=="Running"&&!op.CancelRequested&&op.SessionVerified)return;
        if(job.CancelRequested||job.Status!="Running"||op.CancelRequested||!op.BrowserOpened)throw new ArgumentException("La ventana no está disponible para comprobar el acceso.");
        op.Command="Verify";op.Updated=DateTimeOffset.UtcNow;
        op.Status="InspectingPortal";op.VerificationCode=null;
        ExecutionProgress.Set(state,id,"VerifyingLogin","Comprobando el acceso al SRI. Espere el resultado de la verificación.");
    }
    public static bool AgentOnline(State state) => state.AgentHeartbeat > DateTimeOffset.UtcNow.AddSeconds(-15);
    public static bool Active(AuthenticationOperation op) => op.Status is "Ready" or "OpeningBrowser" or "WaitingForLogin" or "RequiresHumanAction" or "InspectingPortal" || op.Owner=="Agent"&&op.BrowserOpened;
    public static AuthenticationOperation Start(State state, string account)
    {
        var a = state.Accounts.SingleOrDefault(a => a.Id == account) ?? throw new KeyNotFoundException();
        if (a.IsTest) throw new ArgumentException("Las cuentas de prueba no pueden abrir el SRI real.");
        var taxId = string.IsNullOrWhiteSpace(a.TaxId) ? a.Id : a.TaxId;
        if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new ArgumentException("Configure el RUC de 13 dígitos.");
        if (!AgentOnline(state)) throw new ArgumentException("Agente Windows no disponible. La autenticación está pendiente; no se abrió un navegador.");
        if (state.Authentications.Any(a=>Active(a)&&(a.Owner=="Agent"||a.Status!="RequiresHumanAction"))) throw new ArgumentException("Ya hay una autenticación activa. Finalícela o cancélela desde el panel.");
        if (state.Executions.Any(e => e.Query.Account == account && e.Status is "Pending" or "Running")) throw new ArgumentException("Cancele o termine la ejecución de esta cuenta antes de autenticar.");
        var op = new AuthenticationOperation { Account = account };
        state.Authentications.Add(op); return op;
    }
    public static AuthenticationOperation Command(State state, string account, string command)
    {
        var op = state.Authentications.LastOrDefault(a => a.Account == account) ?? throw new ArgumentException("Inicie la autenticación desde el panel.");
        if (command is not "Verify" and not "Reload" and not "Cancel" and not "Logout" and not "RemoteLogout" and not "Show") throw new ArgumentException("Acción inválida.");
        if(command=="Show"&&!op.BrowserOpened)throw new ArgumentException("No hay una ventana SRI disponible.");
        if(command=="Verify"&&op.SessionVerified)return op;
        if(command=="Reload"&&op.SessionVerified)throw new ArgumentException("Cierre el acceso antes de abrir otro; la sesión está en uso o verificada.");
        if (command == "RemoteLogout" && state.Executions.Any(e => e.Query.Account == account && e.Status is "Pending" or "Running")) throw new ArgumentException("Cancele primero la ejecución activa.");
        if (command is "Verify" or "Reload" && !Active(op)) throw new ArgumentException("Abra una nueva sesión interactiva.");
        if (command == "Cancel") { op.CancelRequested = true; op.SessionVerified = false; op.Expires = null; }
        if (command is "Logout" or "RemoteLogout") { op.SessionVerified = false; op.Expires = null; }
        if (command == "RemoteLogout") { op.Status = "InspectingPortal"; op.Message = "Cierre remoto solicitado; aún sin confirmar. No se permiten consultas con esta sesión."; }
        op.Command = command; op.Updated = DateTimeOffset.UtcNow;
        if(op.Owner=="Worker"&&command=="Cancel") {op.Command="";if(op.Status is "Ready" or "RequiresHumanAction" or "Failed" or "Expired")op.Status="Cancelled";}
        return op;
    }
}
