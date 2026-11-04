using Microsoft.AspNetCore.Http;
using SriAutomation.Api;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;
[Collection("Agent environment")]
public class LocalDeliveryTests
{
    [Fact]
    public void QueuePersistsAndIsIdempotentAcrossRepositoryRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "sri-queue-" + Guid.NewGuid().ToString("N"));
        try {
            var r = new JsonStateRepository(path);
            var doc = new Voucher { Source="Sri", Account="a", Type="Facturas", AccessKey=new string('1',49), PdfPath="file.pdf", PdfSha256=new string('a',64) };
            var job = new Execution { Query = new Query("a", new(2026,1,1),new(2026,1,1),["Facturas"],StorageMode:"LOCAL_WINDOWS") };
            r.Update(s => { s.Accounts.Add(new("a","Empresa","1790000000001")); s.Documents.Add(doc); s.Executions.Add(job); LocalDeliveryEndpoints.Queue(s,job,doc,"pdf"); LocalDeliveryEndpoints.Queue(s,job,doc,"pdf"); return true; });
            var state = new JsonStateRepository(path).Read(s => s);
            Assert.Single(state.LocalDeliveries); Assert.Equal("Pending",state.LocalDeliveries[0].Status); Assert.Equal(1,state.Executions[0].DeliveryTotal);
            Assert.Throws<ArgumentException>(() => { doc.Source="Imported"; LocalDeliveryEndpoints.Queue(state,state.Executions[0],doc,"pdf"); });
        } finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(path)) Directory.Delete(path,true); }
    }
    [Fact]
    public void SharePointSelectionRequiresExplicitEnablement()
    {
        var previous=Environment.GetEnvironmentVariable("SRI_SHAREPOINT_ENABLED");
        try {
            Environment.SetEnvironmentVariable("SRI_SHAREPOINT_ENABLED", "false");
            var state=new State(); state.Accounts.Add(new("a","test","1790000000001"));
            var query=new Query("a",new(2025,1,1),new(2025,1,1),["Facturas"],StorageMode:"SHAREPOINT");
            Assert.Throws<ArgumentException>(()=>SriAutomation.Application.Validation.Query(query,state));
            SriAutomation.Application.Validation.Query(query with { StorageMode="LOCAL_WINDOWS" },state);
        } finally { Environment.SetEnvironmentVariable("SRI_SHAREPOINT_ENABLED",previous); }
    }
    [Fact]
    public void AgentRequiresKeyAndRejectsBrowserOrigin()
    {
        var path = Path.GetTempFileName(); var old=Environment.GetEnvironmentVariable("SRI_AGENT_KEY_FILE");
        try {
            var key=new string('a',64); File.WriteAllText(path,key); Environment.SetEnvironmentVariable("SRI_AGENT_KEY_FILE",path);
            var ctx=new DefaultHttpContext(); Assert.False(AgentEndpoints.Authorized(ctx));
            ctx.Request.Headers["X-SRI-Agent-Key"]=key; Assert.True(AgentEndpoints.Authorized(ctx));
            ctx.Request.Headers.Origin="https://example.test"; Assert.False(AgentEndpoints.Authorized(ctx));
        } finally { Environment.SetEnvironmentVariable("SRI_AGENT_KEY_FILE",old); File.Delete(path); }
    }
}
