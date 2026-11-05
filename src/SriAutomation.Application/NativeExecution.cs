using SriAutomation.Domain;
namespace SriAutomation.Application;
public static class NativeExecution
{
    public static Execution Authorized(State state,string id)
    {
        var job=state.Executions.SingleOrDefault(e=>e.Id==id)??throw new KeyNotFoundException();
        var account=state.Accounts.Single(a=>a.Id==job.Query.Account);
        var access=state.Authentications.LastOrDefault(a=>a.ExecutionId==id);
        if(account.IsTest||job.Status!="Running"||job.CancelRequested||access is null||access.CancelRequested||!access.SessionVerified||access.Expires<=DateTimeOffset.UtcNow||access.Expires is null)throw new ArgumentException("La operación no tiene un acceso autorizado activo.");
        return job;
    }
    public static State Snapshot(State state,string id)
    {
        var job=Authorized(state,id);return new(){Executions=[job],Accounts=[state.Accounts.Single(a=>a.Id==job.Query.Account)],Documents=state.Documents.Where(d=>d.Account==job.Query.Account&&d.Issued>=job.Query.From&&d.Issued<=job.Query.To&&job.Query.Types.Contains(d.Type)).ToList()};
    }
    public static void Apply(State state,string id,State delta)
    {
        var job=Authorized(state,id);var update=delta.Executions.Single(e=>e.Id==id);
        if(update.AttemptedKeys.Any(k=>k.Length!=49||!k.All(char.IsAsciiDigit)))throw new ArgumentException("Claves inválidas.");
        if(update.Queries.Any(q=>q.Date<job.Query.From||q.Date>job.Query.To||!job.Query.Types.Contains(q.Type)))throw new ArgumentException("Resultado fuera del alcance solicitado.");
        job.SriCheckpoint=update.SriCheckpoint;job.Queries=update.Queries;job.AttemptedKeys=job.AttemptedKeys.Union(update.AttemptedKeys).ToList();
        job.Checkpoint=update.Checkpoint;job.Downloaded=update.Downloaded;job.Processed=update.Processed;job.Found=update.Found;job.Errors=update.Errors;job.Issues=update.Issues.Take(100).ToList();
        if(new[]{"Querying","Downloading","Processing","RecognizingControls","OpeningQuery","WaitingForHuman","AuthenticatingApplication"}.Contains(update.Stage))ExecutionProgress.Set(state,id,update.Stage,update.Stage switch{"AuthenticatingApplication"=>"Autenticando la aplicación de comprobantes con el perfil autorizado.","WaitingForHuman"=>"El SRI solicita verificación humana. Complétela en su ventana; el bot seguirá al detectarla.","OpeningQuery"=>"Esperando la redirección y apertura de la consulta en la sesión autorizada.","Querying"=>"Consultando comprobantes en el SRI.","Downloading"=>"Descargando PDF.","RecognizingControls"=>"Reconociendo los filtros del SRI.",_=>"Verificando y guardando documentos."});
        foreach(var doc in delta.Documents){var existing=state.Documents.SingleOrDefault(d=>d.Id==doc.Id&&d.Account==job.Query.Account);if(existing is null)continue;existing.Status=doc.Status;existing.Warning=doc.Warning;}
    }
}
