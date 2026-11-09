using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace SriAutomation.Tests;

public class DocumentExplorerBrowserTests
{
    [BrowserFact]
    public async Task ExplorerNavigatesAndShowsManualDownloadsAndZipStates()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../front/dist/sri-front/browser"));
        Assert.True(File.Exists(Path.Combine(root,"index.html")), "Compile el frontend antes de ejecutar la prueba.");
        var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();
        app.MapGet("/api/v1/{**path}", (string path) => path switch {
            "auth/session" => Results.Json(new { user="fixture", role="admin", mustChange=false }),
            "settings" => Results.Json(new { defaultAccount="a" }),
            "accounts" => Results.Json(new[] { new { id="a",name="Ficticia",taxId="1790000000001" } }),
            "document-explorer/a" => Results.Json(new { folders=new[] {new {year=2026,month=10,type="Facturas",count=1}}, reports=new[] {new {year=2026,month=10}} }),
            "document-explorer/a/items" => Results.Json(new { total=1,items=new[] {new {id="d1",type="Facturas",number="001-001-000000001",accessKey=new string('1',49),issued="2026-10-12",issuerName="Emisor ficticio",total=10,status="ValidadoXML",pdfRegistered=true,xmlRegistered=true}} }),
            "document-explorer/a/zip/abc" => Results.Json(new {id="abc",status="Listo",message="",done=2,total=2,issues=Array.Empty<string>(),warnings=Array.Empty<string>()}),
            "documents" => Results.Json(new {total=0,items=Array.Empty<object>()}),
            "worker/status" => Results.Json(new {online=false}),
            _ => Results.Json(Array.Empty<object>())
        });
        app.MapPost("/api/v1/document-explorer/a/zip", () => Results.Json(new {id="abc",status="Preparando",message="Esperando recuperación segura.",done=0,total=2,issues=Array.Empty<string>(),warnings=Array.Empty<string>()}));
        app.MapGet("/{**path}",(string? path) => { var name=string.IsNullOrEmpty(path)?"index.html":path; var file=Path.Combine(root,name); if (!File.Exists(file)) file=Path.Combine(root,"index.html"); var mime=Path.GetExtension(file) switch {".js"=>"text/javascript",".css"=>"text/css",".json"=>"application/json",_=>"text/html"}; return Results.File(file,mime); });
        await app.StartAsync(); var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var playwright=await Playwright.CreateAsync(); await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});
        foreach(var theme in new[]{"light","dark"}) {
            await using var context=await browser.NewContextAsync(); await context.AddInitScriptAsync($"localStorage.setItem('sri-theme','{theme}')"); var page=await context.NewPageAsync();
            var downloads=0;page.Download+=(_,_)=>downloads++;
            await page.GotoAsync(address+"/comprobantes");
            await page.GetByRole(AriaRole.Button,new(){Name="Mis documentos",Exact=true}).ClickAsync();
            var explorer=page.Locator("sri-explorador");
            await explorer.GetByLabel("RUC",new(){Exact=true}).SelectOptionAsync("a");
            await explorer.GetByRole(AriaRole.Button,new(){Name="2026",Exact=true}).ClickAsync();
            await explorer.GetByRole(AriaRole.Button,new(){Name="10 Octubre",Exact=true}).ClickAsync();
            await explorer.GetByRole(AriaRole.Button,new(){Name="Facturas (1)",Exact=true}).ClickAsync();
            await explorer.GetByPlaceholder("Emisor, receptor, número o clave").FillAsync("ficticio");
            await explorer.GetByLabel("Ordenar").SelectOptionAsync("total");
            var filtered=await page.RunAndWaitForRequestAsync(() => explorer.GetByRole(AriaRole.Button,new(){Name="Aplicar",Exact=true}).ClickAsync(), r=>r.Url.Contains("/document-explorer/a/items?") && r.Url.Contains("search=ficticio"));
            Assert.Contains("sort=total",filtered.Url);
            await explorer.GetByRole(AriaRole.Button,new(){Name="Ver detalle",Exact=true}).ClickAsync();
            await Assertions.Expect(explorer.GetByText("Emisor ficticio",new(){Exact=false}).Last).ToBeVisibleAsync();
            await Assertions.Expect(explorer.GetByRole(AriaRole.Link,new(){Name="Descargar PDF"})).ToHaveAttributeAsync("href", "/api/v1/documents/d1/pdf");
            await Assertions.Expect(explorer.GetByRole(AriaRole.Link,new(){Name="Descargar XML"})).ToHaveAttributeAsync("href", "/api/v1/documents/d1/xml");
            await explorer.GetByLabel("Formatos del ZIP").SelectOptionAsync("xml");
            var prepare=await page.RunAndWaitForRequestAsync(() => explorer.GetByRole(AriaRole.Button,new(){Name="Preparar ZIP de esta carpeta"}).ClickAsync(),r=>r.Method=="POST" && r.Url.EndsWith("/document-explorer/a/zip"));
            Assert.Contains("\"formats\":\"xml\"",prepare.PostData);
            await Assertions.Expect(explorer.GetByText("Esperando recuperación segura.")).ToBeVisibleAsync();
            await Assertions.Expect(explorer.GetByRole(AriaRole.Link,new(){Name="Descargar ZIP preparado"})).ToBeVisibleAsync();
            Assert.Equal(theme,await page.Locator("html").GetAttributeAsync("data-theme")); Assert.Equal(0,downloads);
            await explorer.GetByRole(AriaRole.Button,new(){Name="10 Octubre",Exact=true}).ClickAsync();
            await explorer.GetByRole(AriaRole.Button,new(){Name="Reportes",Exact=true}).ClickAsync();
            await Assertions.Expect(explorer.GetByRole(AriaRole.Link,new(){Name="Descargar Excel"})).ToHaveAttributeAsync("href","/api/v1/reports/a/2026/10");
        }
        await app.StopAsync();
    }
}
