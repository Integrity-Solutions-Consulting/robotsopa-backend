using System.Text.Json;
using SriAutomation.Api;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class ZipServiceDoubleTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact]
    public async Task PersistedJobSurvivesServiceRestartAndLostFinishResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "zip-double-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            byte[] bytes = [1,2,3]; File.WriteAllBytes(Path.Combine(root,"a.pdf"),bytes); var hash=DownloadPipeline.Hash(bytes);
            var state = new State { Accounts=[new("a","A","1790000000001")], Documents=[new Voucher { Account="a", Type="Facturas", AccessKey=new string('1',49), Issued=new(2026,10,1), PdfPath="a.pdf", PdfSha256=hash }] };
            var store = new FakeStore { LoseFinishResponse = true }; var repository = new MemoryRepo(state);
            string id;
            using (var service = new DocumentZipService(repository,new DocumentStorage(root),store)) {
                id=Json(service.Start("owner","a",new(2026,10,"Facturas","pdf"))).GetProperty("id").GetString()!;
                await WaitFor(() => store.Find(Guid.Parse(id),"owner","a")?.Status == "Ready");
                Assert.Equal("Listo",Json(service.Status("owner","a",id)).GetProperty("status").GetString());
                Assert.True(store.Find(Guid.Parse(id),"owner","a")!.Processed>0);
            }
            using var restarted = new DocumentZipService(repository,new DocumentStorage(root),store);
            Assert.Equal("Listo",Json(restarted.Status("owner","a",id)).GetProperty("status").GetString());
            Assert.Throws<KeyNotFoundException>(() => restarted.Status("other","a",id));
            Assert.Throws<KeyNotFoundException>(() => restarted.Status("owner","other",id));
            var ready = store.Find(Guid.Parse(id), "owner", "a")!;
            var archive = Path.Combine(Path.GetTempPath(), "sopa-document-zips-v2", ready.InstanceId!.Value.ToString("N"), ready.ArchiveName!);
            File.WriteAllBytes(archive, [9]);
            Assert.Equal("Error", Json(restarted.Status("owner", "a", id)).GetProperty("status").GetString());
            File.Delete(archive);
            Assert.Equal("Error", Json(restarted.Status("owner", "a", id)).GetProperty("status").GetString());
            store.SetExpiredLease();
            var waiting = Json(restarted.Status("owner", "a", id));
            Assert.Equal("Preparando", waiting.GetProperty("status").GetString());
            Assert.Contains("esperando recuperación", waiting.GetProperty("message").GetString());
            Assert.Equal("Preparing", store.Find(Guid.Parse(id), "owner", "a")!.Status);
        } finally { Directory.Delete(root,true); }
    }
    private static async Task WaitFor(Func<bool> condition) { for (var i=0;i<200;i++) { if(condition()) return; await Task.Delay(25); } throw new TimeoutException(); }
    private sealed class MemoryRepo(State state) : IStateRepository { public T Read<T>(Func<State,T> f)=>f(state); public T Update<T>(Func<State,T> f)=>f(state); }
    private sealed class FakeStore : IZipJobStore
    {
        private readonly object gate=new(); private StoredZipJob? job; private ZipJobLease? lease; public bool LoseFinishResponse;
        public void SetExpiredLease() { lock(gate) job=job! with { Status="Preparing", LeaseExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1) }; }
        public void Create(Guid id,string user,string account,string selectionJson,string signature,int expected,DateTimeOffset expiresAt) { lock(gate) job=new(id,user,account,selectionJson,signature,"Pending",expected,0,0,"[]","[]",null,null,null,DateTimeOffset.UtcNow,expiresAt,null); }
        public ZipJobLease? Claim(Guid id,Guid instance,TimeSpan duration) { lock(gate) { if(job?.Id!=id || job.Status!="Pending")return null; lease=new(id,instance,Guid.NewGuid(),1); job=job with {Status="Preparing",InstanceId=instance}; return lease; } }
        public bool Renew(ZipJobLease value,TimeSpan duration) {lock(gate)return lease==value && job?.Status=="Preparing";}
        public bool Progress(ZipJobLease value,int processed,int omitted) { lock(gate) {if(lease!=value || job is null || job.Status!="Preparing")return false;job=job with {Processed=processed,Omitted=omitted};return true;} }
        public bool Finish(ZipJobLease value,string status,string? name,string? hash,long? size,string issues,string warnings) { lock(gate) {if(lease!=value || job is null || job.Status!="Preparing")return false;job=job with {Status=status,ArchiveName=name,ArchiveSha256=hash,ArchiveSize=size,Issues=issues,Warnings=warnings}; if(LoseFinishResponse && status=="Ready") {LoseFinishResponse=false;throw new IOException("simulated lost response");} return true;} }
        public StoredZipJob? Find(Guid id,string user,string account) { lock(gate)return job?.Id==id && job.User==user && job.Account==account ? job : null; }
        public StoredZipJob? FindForProcessing(Guid id) {lock(gate)return job?.Id==id?job:null;}
        public StoredZipJob? FindReusable(string user,string account,string signature) {lock(gate)return job?.User==user && job.Account==account && job.Signature==signature ? job : null;}
        public bool Authorized(string user,string account)=>user=="owner"&&account=="a";
        public void Expire() { }
        public IReadOnlyList<Guid> Recoverable(int limit=100) {lock(gate)return job?.Status=="Pending"?[job.Id]:[];}
    }
}
