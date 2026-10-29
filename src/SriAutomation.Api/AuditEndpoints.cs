using SriAutomation.Application;
namespace SriAutomation.Api;
public static class AuditEndpoints
{
    public static void RecordRequest(HttpContext c,IStateRepository repository)
    {
        if(c.Items["PanelUser"] is not string user || c.Request.Method is "GET" or "HEAD" || !c.Request.Path.StartsWithSegments("/api/v1"))return;
        var path=c.Request.Path.Value!;var method=c.Request.Method;
        var action=path switch {
            "/api/v1/auth/login"=>"Inicio de sesión del panel", "/api/v1/auth/logout"=>"Cierre de sesión del panel",
            "/api/v1/accounts/register"=>"Registro de cuenta SRI", "/api/v1/accounts"=>"Registro de cuenta SRI",
            "/api/v1/users"=>"Creación o activación de usuario",
            "/api/v1/reports"=>"Generación de Excel", "/api/v1/executions" or "/api/v1/comprobantes" or "/api/v1/connection-tests"=>"Solicitud de descarga o procesamiento",
            "/api/v1/settings"=>"Cambio de configuración", "/api/v1/schedules"=>"Creación de programación",
            _ when path.EndsWith("/cancel")=>"Cancelación de ejecución", _ when path.EndsWith("/retry")=>"Reintento de ejecución", _ when path.EndsWith("/resume")=>"Reanudación de ejecución",
            _ when path.EndsWith("/connect")=>"Conexión autorizada al SRI", _ when path.EndsWith("/logout")=>"Revocación de sesión SRI",
            _ when path.EndsWith("/credentials")=>method=="DELETE"?"Eliminación de credencial cifrada":"Actualización de credencial cifrada",
            _ when path.EndsWith("/ready")=>"Comprobación de acceso solicitada",
            _ when path.StartsWith("/api/v1/accounts/")=>method=="DELETE"?"Eliminación de cuenta SRI":"Modificación o verificación de cuenta SRI",
            _ when path.StartsWith("/api/v1/schedules/")=>method=="DELETE"?"Eliminación de programación":"Modificación o ejecución de programación",
            _ when path.StartsWith("/api/v1/documents/")=>"Importación o revisión de documentos",
            _ => "Cambio de configuración de operación"
        };
        try { repository.Update(s=> {
            var account=c.Items["AuditAccount"] as string ?? c.Request.RouteValues["account"]?.ToString();
            if(c.Request.RouteValues["id"]?.ToString() is {} id) {
                if(path.StartsWith("/api/v1/accounts/")) account=id;
                else account ??= s.Executions.FirstOrDefault(e=>e.Id==id)?.Query.Account ?? s.Schedules.FirstOrDefault(e=>e.Id==id)?.Query.Account ?? s.Documents.FirstOrDefault(d=>d.Id==id)?.Account;
            }
            AuditTrail.Add(s,user,action,account,c.Response.StatusCode<400?"Solicitud completada":"Error",c.Request.Headers["X-SRI-Test-Data"]=="1"||c.Items["AuditIsTest"] is true);
            if(c.Items["AuditAccountName"] is string name)s.Audit[^1]=s.Audit[^1] with{AccountName=name};return true;
        }); } catch(IOException) { c.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Audit").LogError("No se pudo persistir un evento de auditoría."); }
    }
}
