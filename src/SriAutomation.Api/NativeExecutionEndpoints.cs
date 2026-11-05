using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
namespace SriAutomation.Api;
public static class NativeExecutionEndpoints
{
    public static void MapNativeExecution(this WebApplication app)
    {
        var agent=app.MapGroup("/api/internal/agent/executions");
        agent.MapGet("/{id}",(string id,IStateRepository r)=>r.Read(s=>NativeExecution.Snapshot(s,id)));
        agent.MapPut("/{id}",(string id,State delta,IStateRepository r)=>r.Update(s=>{NativeExecution.Apply(s,id,delta);return new{saved=true};}));
        agent.MapGet("/{id}/available/{document}/{extension}",(string id,string document,string extension,IStateRepository r,IStorageProvider storage,IDocumentParser parser)=>{
            var job=r.Read(s=>NativeExecution.Authorized(s,id));
            if(extension is not "xml" and not "pdf")throw new ArgumentException("Formato inválido.");
            var doc=r.Read(s=>s.Documents.Single(d=>d.Id==document&&d.Account==job.Query.Account));
            return new DownloadPipeline(r,storage,parser).IsAvailable(doc,extension);
        });
        agent.MapPost("/{id}/files",(string id,DownloadedVoucher file,IStateRepository r,IStorageProvider storage,IDocumentParser parser)=>{
            var job=r.Read(s=>NativeExecution.Authorized(s,id));
            if(file.Account!=job.Query.Account||file.Issued<job.Query.From||file.Issued>job.Query.To||!job.Query.Types.Contains(file.Type)||!job.AttemptedKeys.Contains(file.Key))throw new ArgumentException("El archivo está fuera de la consulta autorizada.");
            return new DownloadPipeline(r,storage,parser).Process(file);
        });
    }
}
