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
            if (job.Query.StorageMode == "LOCAL_WINDOWS" && doc.Source != "Sri") return false;
            var available = new DownloadPipeline(r,storage,parser).IsAvailable(doc,extension);
            if (available && job.Query.StorageMode == "LOCAL_WINDOWS") {
                var path = extension == "xml" ? doc.XmlPath : doc.PdfPath; var hash = extension == "xml" ? doc.XmlSha256 : doc.PdfSha256;
                available = path is not null && hash is not null && File.Exists(storage.Resolve(path)) && DownloadPipeline.Hash(File.ReadAllBytes(storage.Resolve(path))) == hash;
            }
            if (available && job.Query.StorageMode == "LOCAL_WINDOWS") r.Update(s => { LocalDeliveryEndpoints.Queue(s, s.Executions.Single(e => e.Id == id), s.Documents.Single(d => d.Id == document), extension); return true; });
            return available;
        });
        agent.MapPost("/{id}/files",(string id,DownloadedVoucher file,IStateRepository r,IStorageProvider storage,IDocumentParser parser)=>{
            var job=r.Read(s=>NativeExecution.Authorized(s,id));
            if(file.Account!=job.Query.Account||file.Issued<job.Query.From||file.Issued>job.Query.To||!job.Query.Types.Contains(file.Type)||!job.AttemptedKeys.Contains(file.Key))throw new ArgumentException("El archivo está fuera de la consulta autorizada.");
            var doc = new DownloadPipeline(r,storage,parser).Process(file);
            r.Update(s => { var current=s.Documents.Single(d => d.Id == doc.Id); var execution=s.Executions.Single(e => e.Id == id); if(file.Xml is not null) LocalDeliveryEndpoints.Queue(s, execution, current, "xml"); if(file.Pdf is not null) LocalDeliveryEndpoints.Queue(s, execution, current, "pdf"); return true; });
            return doc;
        });
    }
}
