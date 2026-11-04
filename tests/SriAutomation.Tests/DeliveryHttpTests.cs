using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SriAutomation.Api;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;
[Collection("Agent environment")]
public class DeliveryHttpTests
{
    [Fact]
    public async Task AgentPollsActualApiAndConfirmsSavedFile()
    {
        var root=Path.Combine(Path.GetTempPath(),"sri-http-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var keyFile=Path.Combine(root,"agent.key"); var previous=Environment.GetEnvironmentVariable("SRI_AGENT_KEY_FILE");
        WebApplication? app=null;
        try {
            const string key="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; File.WriteAllText(keyFile,key); Environment.SetEnvironmentVariable("SRI_AGENT_KEY_FILE",keyFile);
            byte[] bytes=[1,2,3]; var storage=new DocumentStorage(Path.Combine(root,"server")); Directory.CreateDirectory(Path.Combine(root,"server")); File.WriteAllBytes(Path.Combine(root,"server","fixture.pdf"),bytes);
            var repo=new MemoryRepository();
            var doc=new Voucher{Source="Sri",Account="a",Type="Facturas",AccessKey=new string('1',49),Issued=new DateOnly(2026,4,1),PdfPath="fixture.pdf",PdfSha256=DownloadPipeline.Hash(bytes)};
            var job=new Execution{Status="WaitingForDelivery",Query=new Query("a",new(2026,4,1),new(2026,4,1),["Facturas"],StorageMode:"LOCAL_WINDOWS")};
            repo.Update(s=>{s.Accounts.Add(new("a","test","1790000000001"));s.Documents.Add(doc);s.Executions.Add(job);LocalDeliveryEndpoints.Queue(s,job,doc,"pdf");return true;});
            var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Services.AddSingleton<IStateRepository>(repo); builder.Services.AddSingleton<IStorageProvider>(storage);
            app=builder.Build(); app.Use(async (ctx,next)=>{if(!AgentEndpoints.Authorized(ctx)){ctx.Response.StatusCode=401;return;}await next();}); app.MapLocalDelivery(); await app.StartAsync();
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http=new HttpClient{BaseAddress=new Uri(address)};
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized,(await http.GetAsync("/api/internal/agent/deliveries/next")).StatusCode);
            http.DefaultRequestHeaders.Add("X-SRI-Agent-Key",key);
            await new DeliveryClient(http,new WindowsDesktopStorage(Path.Combine(root,"desktop"))).Tick(CancellationToken.None);
            var state=repo.Read(s=>s); Assert.Equal("Completed",state.LocalDeliveries.Single().Status); Assert.Equal("Completed",state.Executions.Single().Status);
            Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(root,"desktop","SRI-Automation","1790000000001","2026","04","Facturas",doc.AccessKey+".pdf")));
        } finally {
            if(app is not null) await app.DisposeAsync(); Environment.SetEnvironmentVariable("SRI_AGENT_KEY_FILE",previous); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true);
        }
    }
    private sealed class MemoryRepository : IStateRepository
    {
        private readonly State state = new();
        public T Read<T>(Func<State,T> read) { lock(state) return read(state); }
        public T Update<T>(Func<State,T> mutate) { lock(state) return mutate(state); }
    }
}
