using System.Text.Json;
using Microsoft.Playwright;
namespace SriAutomation.Tests;

// Explicit engineering opt-in only. The normal suite never contacts SRI.
public sealed class RealSriControlledFactAttribute:FactAttribute
{
    public RealSriControlledFactAttribute(){if(Environment.GetEnvironmentVariable("SRI_REAL_CONTROLLED_ACCOUNT") is null)Skip="Requires explicit authorization of the controlled real SRI scope.";}
}
public sealed class RealSriControlledTests
{
    [RealSriControlledFact]
    public async Task AngularStartsTheAuthorizedSingleDayInvoiceOperation()
    {
        var account=Environment.GetEnvironmentVariable("SRI_REAL_CONTROLLED_ACCOUNT")!;
        var root=Directory.GetCurrentDirectory();while(!File.Exists(Path.Combine(root,".env")))root=Directory.GetParent(root)?.FullName??throw new InvalidOperationException("Workspace missing.");
        var secret=File.ReadLines(Path.Combine(root,".env")).Single(l=>l.StartsWith("SRI_ADMIN_PASSWORD=")).Split('=',2)[1];
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});
        var context=await browser.NewContextAsync();var page=await context.NewPageAsync();
        page.Dialog+=(_,dialog)=>_ = dialog.AcceptAsync();
        await page.GotoAsync("http://localhost:3000");await page.GetByLabel("Usuario",new(){Exact=true}).FillAsync(File.ReadLines(Path.Combine(root,".env")).Single(l=>l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=',2)[1]);await page.GetByLabel("Contraseña del panel").FillAsync(secret);await page.GetByRole(AriaRole.Button,new(){Name="Entrar al panel",Exact=true}).ClickAsync();
        await page.GetByRole(AriaRole.Link,new(){Name="Comprobantes",Exact=true}).ClickAsync();
        var form=page.Locator("app-query-form");await form.GetByLabel("Cuenta SRI",new(){Exact=true}).SelectOptionAsync(account);
        await form.GetByLabel("Fecha inicial").FillAsync("2026-10-12");await form.GetByLabel("Fecha final").FillAsync("2026-10-12");
        var response=await page.RunAndWaitForResponseAsync(()=>form.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync(),r=>r.Request.Method=="POST"&&r.Url.EndsWith("/executions"));
        Assert.True(response.Ok,"Angular could not start the controlled operation.");
        var created=(await response.JsonAsync())!.Value;var id=created.GetProperty("id").GetString();var query=created.GetProperty("query");
        Assert.Equal("2026-10-12",query.GetProperty("from").GetString());Assert.Equal("2026-10-12",query.GetProperty("to").GetString());Assert.Equal("Sri",query.GetProperty("mode").GetString());Assert.Equal(10,query.GetProperty("maxDocuments").GetInt32());Assert.Equal("Facturas",Assert.Single(query.GetProperty("types").EnumerateArray()).GetString());
        JsonElement job=default;
        for(var i=0;i<150;i++){
            var result=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/executions/"+id);job=(await result.JsonAsync())!.Value;
            if(job.GetProperty("status").GetString() is not "Pending" and not "Running"||job.GetProperty("stage").GetString()=="WaitingForHuman")break;
            await Task.Delay(1000);
        }
        var evidence=new {at=DateTimeOffset.UtcNow,execution=id,status=job.GetProperty("status").GetString(),stage=job.GetProperty("stage").GetString(),found=job.GetProperty("found").GetInt32(),processed=job.GetProperty("processed").GetInt32(),errors=job.GetProperty("errors").GetInt32(),reports=job.GetProperty("reports").GetArrayLength()};
        await File.WriteAllTextAsync(Path.Combine(root,".secrets","real-validation.json"),JsonSerializer.Serialize(evidence));
        Assert.True(evidence.status=="Completed",$"Real SRI pending: status={evidence.status}, stage={evidence.stage}. No fiscal success declared.");
        Assert.True(evidence.reports>0,"No monthly report was produced.");
        await Assertions.Expect(page.Locator("sri-consulta .badge")).ToHaveTextAsync("Terminado con éxito");
        var report=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/reports/"+account+"/2026/10");Assert.True(report.Ok);Assert.True((await report.BodyAsync()).Length>0);
    }
}
