using SriAutomation.Application;
using SriAutomation.Domain;
namespace SriAutomation.Automation;
// No recibe ni almacena claves SRI. Cada operación solicita un acceso nuevo.
public sealed class WorkerAuthentication(IStateRepository repository)
{
 public async Task<SessionData> Connect(Account account,CancellationToken cancellation,string? executionId=null)
 {
  if(account.IsTest)throw new HumanActionRequiredException("Las cuentas de prueba no pueden acceder al SRI real.");
  if(!repository.Read(AuthenticationFlow.AgentOnline))throw new HumanActionRequiredException("El agente local no está conectado. Abra la aplicación local desde Angular antes de reanudar.");
  // A paused diagnostic window must be acknowledged closed before opening another login.
  var paused=repository.Update(s=>{var old=s.Authentications.LastOrDefault(a=>a.Account==account.Id&&executionId is not null&&a.ExecutionId==executionId&&a.Status=="QueryFailed"&&a.BrowserOpened);if(old is null)return (string?)null;old.CancelRequested=true;old.Command="Cancel";return old.Id;});
  if(paused is not null){for(var i=0;i<10;i++){await Task.Delay(1000,cancellation);if(!repository.Read(s=>s.Authentications.Single(a=>a.Id==paused).BrowserOpened))break;}}
  var op=repository.Update(s=>{
   if(s.Authentications.Any(a=>AuthenticationFlow.Active(a)))throw new HumanActionRequiredException("Ya hay un acceso abierto. Finalícelo o cancélelo desde Angular.");
   var a=new AuthenticationOperation{Account=account.Id,ExecutionId=executionId,Owner="Agent",Command="Open",Message="Introduzca su clave únicamente en la ventana oficial del SRI."};s.Authentications.Add(a);
   if(executionId is not null)ExecutionProgress.Set(s,executionId,"OpeningBrowser","Abriendo el portal oficial del SRI para iniciar sesión.");
   return a;
  });
  try {
   for(var wait=0;wait<600;wait++) {
    await Task.Delay(1000,cancellation);
    var local=repository.Read(s=>s.Authentications.Single(a=>a.Id==op.Id));
    if(wait>20&&!repository.Read(AuthenticationFlow.AgentOnline))throw new HumanActionRequiredException("El agente Windows se desconectó. La operación se detuvo sin declarar éxito.");
    if(local.SessionVerified&&local.Status=="Authenticated")return SriPortal.Vault().Load(account.Id,account.TaxId??account.Id);
    if(local.Status=="Cancelled")throw new OperationCanceledException("Ventana SRI cerrada.");
    if(local.Status is "Failed" or "PortalUnavailable" or "PendingValidation")throw new HumanActionRequiredException("No se pudo confirmar el acceso al portal oficial. La consulta sigue pendiente.");
   }
   throw new HumanActionRequiredException("Se agotó el tiempo para iniciar sesión en el SRI. La consulta no se ejecutó.");
  } finally {repository.Update(s=>{var a=s.Authentications.Single(a=>a.Id==op.Id);if(!a.SessionVerified){a.CancelRequested=true;a.Command="Cancel";}return true;});}
 }
 public Task<bool> Tick(CancellationToken stop){
  if (!repository.Read(s=>s.Authentications.Any(a=>a.SessionVerified&&a.Expires<=DateTimeOffset.UtcNow))) return Task.FromResult(false);
  repository.Update(s=>{foreach(var op in s.Authentications.Where(a=>a.SessionVerified&&a.Expires<=DateTimeOffset.UtcNow)){SriPortal.Vault().Revoke(op.Account);op.SessionVerified=false;op.Expires=null;op.Status="Expired";}return true;});
  return Task.FromResult(false);
 }
}
