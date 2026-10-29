using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Playwright;
using SriAutomation.Domain;
namespace SriAutomation.Tests;
public sealed class FrontendOperationsTests
{
    [LocalStackFact]
    public async Task ClosingAngularDoesNotCancelWorkerOrExcelGeneration()
    {
        var secret=File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_PASSWORD=")).Split('=',2)[1];
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync(new(){ExtraHTTPHeaders=new Dictionary<string,string>{{"X-SRI-Test-Data","1"}}});
        var page=await context.NewPageAsync();await page.GotoAsync("http://localhost:3000");await page.GetByLabel("Usuario",new(){Exact=true}).FillAsync(File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=',2)[1]);await page.GetByLabel("Contraseña del panel").FillAsync(secret);await page.GetByRole(AriaRole.Button,new(){Name="Entrar al panel",Exact=true}).ClickAsync();await page.GetByRole(AriaRole.Heading,new(){Name="Inicio",Exact=true}).WaitForAsync();
        var id="qa-closed-"+Guid.NewGuid().ToString("N")[..8];
        var registered=await context.APIRequest.PostAsync("http://localhost:3000/api/v1/accounts",new(){DataObject=new Account(id,"FICTICIA cierre Angular")});Assert.True(registered.Ok);
        var imported=await context.APIRequest.PostAsync("http://localhost:3000/api/v1/documents/import/"+id,new(){Data=Encoding.UTF8.GetString(PhaseTwoTests.Xml()),Headers=new Dictionary<string,string>{{"Content-Type","application/xml"}}});Assert.True(imported.Ok);
        await page.GetByRole(AriaRole.Link,new(){Name="Comprobantes",Exact=true}).ClickAsync();var form=page.Locator("app-query-form");await form.GetByLabel("Cuenta SRI",new(){Exact=true}).SelectOptionAsync(id);await form.GetByLabel("Fecha inicial").FillAsync("2026-09-12");await form.GetByLabel("Fecha final").FillAsync("2026-09-12");await form.GetByText("Opciones adicionales",new(){Exact=true}).ClickAsync();await form.GetByLabel("Operación").SelectOptionAsync("Stored");
        var response=await page.RunAndWaitForResponseAsync(()=>form.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync(),r=>r.Request.Method=="POST"&&r.Url.EndsWith("/executions"));Assert.True(response.Ok);var jobId=(await response.JsonAsync())!.Value.GetProperty("id").GetString();
        await page.CloseAsync();Assert.True(page.IsClosed);
        JsonElement job=default;for(var i=0;i<30;i++){var result=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/executions/"+jobId);job=(await result.JsonAsync())!.Value;if(job.GetProperty("status").GetString() is not "Pending" and not "Running")break;await Task.Delay(1000);}
        Assert.Equal("Completed",job.GetProperty("status").GetString());Assert.False(job.GetProperty("cancelRequested").GetBoolean());Assert.Equal(1,job.GetProperty("reports").GetArrayLength());
        var report=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/reports/"+id+"/2026/9");Assert.True(report.Ok);using var book=new XLWorkbook(new MemoryStream(await report.BodyAsync()));Assert.Equal(3,book.Worksheets.Count);Assert.Equal("0012345678",book.Worksheet("Documentos").Cell(2,5).GetString());
    }
    [LocalStackFact]
    public async Task DownloadWaitsForManualLoginAndReadyDoesNotClaimSuccess()
    {
        var password=File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_PASSWORD=")).Split('=',2)[1];
        using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});
        var page=await browser.NewPageAsync();page.Dialog+=(_,d)=>_ = d.AcceptAsync();Execution? job=null;var starts=0;var ready=0;
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Only the waiting operation is simulated. No invoice request reaches the SRI.
        await page.RouteAsync("**/api/v1/executions",async route=>{
            if(route.Request.Method=="GET"){await route.FulfillAsync(new(){ContentType="application/json",Body="[]"});return;}
            Assert.Equal("POST",route.Request.Method);starts++;
            var query=JsonSerializer.Deserialize<Query>(route.Request.PostData!,options)!;
            job=new Execution{Id="manual-ui-fixture",Query=query,Status="Running",Stage="WaitingForLogin"};
            await route.FulfillAsync(new(){ContentType="application/json",Body=JsonSerializer.Serialize(job,options)});
        });
        await page.RouteAsync("**/api/v1/executions/manual-ui-fixture",r=>r.FulfillAsync(new(){ContentType="application/json",Body=JsonSerializer.Serialize(job,options)}));
        await page.RouteAsync("**/api/v1/executions/manual-ui-fixture/ready",r=>{ready++;job!.Stage="AccessRejected";job.Message="El formulario de acceso sigue visible. Termine de iniciar sesión.";return r.FulfillAsync(new(){ContentType="application/json",Body="{\"verified\":false}"});});
        await page.GotoAsync("http://localhost:3000");await page.GetByLabel("Usuario",new(){Exact=true}).FillAsync(File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=',2)[1]);await page.GetByLabel("Contraseña del panel").FillAsync(password);await page.GetByRole(AriaRole.Button,new(){Name="Entrar al panel",Exact=true}).ClickAsync();
        await page.GetByRole(AriaRole.Link,new(){Name="Comprobantes",Exact=true}).ClickAsync();
        var form=page.Locator("app-query-form");var account=form.GetByLabel("Cuenta SRI",new(){Exact=true});
        await Assertions.Expect(account.Locator("option")).Not.ToHaveCountAsync(1);await account.SelectOptionAsync(new SelectOptionValue{Index=1});await form.GetByLabel("Fecha inicial").FillAsync("2026-10-12");await form.GetByLabel("Fecha final").FillAsync("2026-10-12");
        await form.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button,new(){Name="Listo, continuar",Exact=true})).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("sri-consulta input[type=password]")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button,new(){Name="Listo, continuar",Exact=true}).ClickAsync();
        Assert.Equal(1,starts);Assert.Equal(1,ready);Assert.NotNull(job);
        Assert.Equal(new DateOnly(2026,10,12),job.Query.From);Assert.Equal(new[]{"Facturas"},job.Query.Types);Assert.True(job.Query.Pdf);Assert.False(job.Query.Xml);
        await Assertions.Expect(page.Locator("sri-consulta .badge")).ToHaveTextAsync("No se confirmó el acceso");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("El formulario de acceso sigue visible");
    }
    [LocalStackFact]
    public async Task FailedConnectionDoesNotRequestManualLoginOrRetryAutomatically()
    {
        var password=File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_PASSWORD=")).Split('=',2)[1];
        using var playwright=await Playwright.CreateAsync();
        await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});
        var page=await browser.NewPageAsync();var mutations=0;
        page.Request+=(_,r)=>{if(r.Method=="POST"&&(r.Url.EndsWith("/connect")||r.Url.EndsWith("/authentication")))mutations++;};
        // Simulate only the failure response; authentication and panel navigation use the local API.
        await page.RouteAsync("**/accounts/*/session",r=>r.FulfillAsync(new(){ContentType="application/json",Body="{\"status\":\"Failed\",\"verified\":false,\"present\":false,\"agentOnline\":true,\"message\":\"SRI-NAV-TIMEOUT\"}"}));
        await page.GotoAsync("http://localhost:3000");
        await page.GetByLabel("Usuario",new(){Exact=true}).FillAsync(File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=',2)[1]);await page.GetByLabel("Contraseña del panel").FillAsync(password);
        await page.GetByRole(AriaRole.Button,new(){Name="Entrar al panel"}).ClickAsync();
        await page.GetByRole(AriaRole.Link,new(){Name="Configuración",Exact=true}).ClickAsync();
        var select=page.GetByLabel("Cuenta SRI",new(){Exact=true});
        await Assertions.Expect(select.Locator("option")).Not.ToHaveCountAsync(1);
        await select.SelectOptionAsync(new SelectOptionValue{Index=1});
        await Assertions.Expect(page.GetByTestId("account-state")).ToHaveTextAsync("Error de conexión");
        await Assertions.Expect(page.GetByRole(AriaRole.Button,new(){Name="Completar verificación humana",Exact=true})).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Button,new(){Name="Ya completé la verificación",Exact=true})).ToHaveCountAsync(0);
        Assert.Equal(0,mutations);
    }
    [LocalStackFact]
    public async Task FourSectionsEncryptedProfilesMultiselectWorkerExcelThemeAndAudit()
    {
        var root=FindRoot();
        var password=File.ReadLines(Path.Combine(root,".env")).Single(l=>l.StartsWith("SRI_ADMIN_PASSWORD=")).Split('=',2)[1];
        var actor="Cristopher";
        const string dummy="FICTICIA-QA-NEVER-REAL-CREDENTIAL";
        using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync(new(){AcceptDownloads=true,ViewportSize=new(){Width=1440,Height=1000},ExtraHTTPHeaders=new Dictionary<string,string>{{"X-SRI-Test-Data","1"}}});
        var page=await context.NewPageAsync();page.Dialog+=(_,d)=>_ = d.AcceptAsync();var errors=new List<string>();page.PageError+=(_,e)=>errors.Add(e);
        var realConnections=0;page.Request+=(_,r)=>{if(r.Method=="POST"&&(r.Url.EndsWith("/connect")||r.Url.EndsWith("/authentication")))realConnections++;};
        await page.GotoAsync("http://localhost:3000");await page.GetByLabel("Usuario",new(){Exact=true}).FillAsync(File.ReadLines(Path.Combine(FindRoot(),".env")).Single(l=>l.StartsWith("SRI_ADMIN_USERNAME=")).Split('=',2)[1]);await page.GetByLabel("Contraseña del panel").FillAsync(password);await page.GetByRole(AriaRole.Button,new(){Name="Entrar al panel"}).ClickAsync();
        await page.GetByRole(AriaRole.Heading,new(){Name="Inicio",Exact=true}).WaitForAsync();
        await Assertions.Expect(page.Locator("aside nav a")).ToHaveCountAsync(4);
        Assert.Equal(new[]{"Inicio","Comprobantes","Historial","Configuración"},await page.Locator("aside nav a").AllTextContentsAsync());
        await Nav("Configuración");
        await Assertions.Expect(page.GetByLabel("Contraseña SRI",new(){Exact=true})).ToBeVisibleAsync();
        Assert.Contains((await context.APIRequest.PostAsync("http://localhost:3000/api/v1/accounts/secure",new(){DataObject=new{}})).Status,new[]{404,405});
        var initial=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/settings");var original=(await initial.JsonAsync())!.Value.Clone();
        try {
        await page.GetByLabel("Alias descriptivo",new(){Exact=true}).FillAsync("QA segura "+Guid.NewGuid().ToString("N")[..8]);
        await page.GetByLabel("RUC del emisor").FillAsync("1790000000001");
        await page.GetByLabel("Contraseña SRI",new(){Exact=true}).FillAsync(dummy);
        var created=await page.RunAndWaitForResponseAsync(()=>page.GetByRole(AriaRole.Button,new(){Name="Guardar cuenta",Exact=true}).ClickAsync(),r=>r.Url.EndsWith("/accounts/register")&&r.Request.Method=="POST");
        Assert.True(created.Ok);var account=(await created.JsonAsync())!.Value;var id=account.GetProperty("id").GetString()!;Assert.True(account.GetProperty("isTest").GetBoolean());
        Assert.Contains("password",created.Request.PostData??"",StringComparison.OrdinalIgnoreCase);
        Assert.Equal(404,(await context.APIRequest.PutAsync("http://localhost:3000/api/v1/accounts/"+id+"/credential",new(){DataObject=new{}})).Status);
        Assert.DoesNotContain(dummy,await created.TextAsync());Assert.False(account.TryGetProperty("password",out _));
        Assert.Equal(400,(await context.APIRequest.PostAsync("http://localhost:3000/api/v1/accounts/register",new(){DataObject=new{alias="FICTICIA rechazada",taxId="1790000000001",unexpected="rejected"}})).Status);
        await Assertions.Expect(page.GetByTestId("account-state")).ToHaveTextAsync("Cuenta registrada");
        await Assertions.Expect(page.GetByRole(AriaRole.Button,new(){Name="Conectar al SRI",Exact=true})).ToBeDisabledAsync();
        var protectedStatus=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/accounts/"+id+"/credentials");Assert.True((await protectedStatus.JsonAsync())!.Value.GetProperty("configured").GetBoolean());Assert.DoesNotContain(dummy,await protectedStatus.TextAsync());
        await Assertions.Expect(page.GetByLabel("Cuenta SRI",new(){Exact=true})).ToHaveValueAsync(id);await Assertions.Expect(page.GetByLabel("Nueva contraseña SRI",new(){Exact=true})).ToBeEnabledAsync();
        await page.GetByLabel("Nueva contraseña SRI",new(){Exact=true}).FillAsync(dummy+"-ROTADA");await page.GetByRole(AriaRole.Button,new(){Name="Guardar nueva contraseña",Exact=true}).ClickAsync();await Assertions.Expect(page.GetByLabel("Nueva contraseña SRI",new(){Exact=true})).ToHaveValueAsync("");
        await page.GetByRole(AriaRole.Button,new(){Name="Eliminar contraseña guardada",Exact=true}).ClickAsync();await Assertions.Expect(page.GetByText("Sin contraseña guardada",new(){Exact=true})).ToBeVisibleAsync();
        await page.GetByLabel("Alias de la cuenta",new(){Exact=true}).FillAsync("QA FRONTEND segura");await page.GetByRole(AriaRole.Button,new(){Name="Guardar cambios de cuenta"}).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Alias de la cuenta",new(){Exact=true})).ToHaveValueAsync("QA FRONTEND segura");
        await page.GetByLabel("Apariencia").SelectOptionAsync("dark");await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme","dark");
        await page.EmulateMediaAsync(new(){ColorScheme=ColorScheme.Dark});await page.GetByLabel("Apariencia").SelectOptionAsync("auto");await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme","dark");
        await page.ReloadAsync();await page.GetByRole(AriaRole.Heading,new(){Name="Configuración",Exact=true}).WaitForAsync();await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme","dark");
        await page.GetByLabel("Cuenta SRI",new(){Exact=true}).SelectOptionAsync(id);
        await page.GetByLabel("Apariencia").SelectOptionAsync("light");
        await page.GetByRole(AriaRole.Button,new(){Name="Usar como predeterminada"}).ClickAsync();
        await Nav("Comprobantes");
        var tools=page.GetByRole(AriaRole.Group,new(){Name="Herramientas de comprobantes"});
        await tools.GetByRole(AriaRole.Button,new(){Name="Documentos descargados",Exact=true}).ClickAsync();
        await page.Locator("sri-documentos").WaitForAsync();await page.Locator("sri-documentos").GetByLabel("Cuenta").SelectOptionAsync(id);
        const string key="1210202601179000000000110010010000000011234567811";
        var xml=Encoding.UTF8.GetBytes($"""
        <autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>{key}</numeroAutorizacion><fechaAutorizacion>2026-10-12T09:00:00-05:00</fechaAutorizacion><comprobante><![CDATA[<factura><infoTributaria><razonSocial>EMISOR FICTICIO QA</razonSocial><ruc>1790000000001</ruc><claveAcceso>{key}</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria><infoFactura><fechaEmision>12/10/2026</fechaEmision><razonSocialComprador>RECEPTOR FICTICIO FRONTEND</razonSocialComprador><identificacionComprador>0012345678</identificacionComprador><totalSinImpuestos>10.00</totalSinImpuestos><totalConImpuestos><totalImpuesto><valor>1.50</valor></totalImpuesto></totalConImpuestos><importeTotal>11.50</importeTotal></infoFactura></factura>]]></comprobante></autorizacion>
        """);
        var imported=await page.RunAndWaitForResponseAsync(()=>page.GetByLabel("Archivo XML").SetInputFilesAsync(new FilePayload{Name="FICTICIO.xml",MimeType="application/xml",Buffer=xml}),r=>r.Url.EndsWith("/documents/import/"+id));
        Assert.True(imported.Ok);var doc=JsonSerializer.Deserialize<Voucher>(await imported.TextAsync(),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await Assertions.Expect(page.GetByRole(AriaRole.Cell,new(){Name="RECEPTOR FICTICIO FRONTEND",Exact=true})).ToBeVisibleAsync();
        var pdf=PdfFixture.Build(key);var attached=await page.RunAndWaitForResponseAsync(()=>page.GetByLabel("Asociar PDF").SetInputFilesAsync(new FilePayload{Name="FICTICIO.pdf",MimeType="application/pdf",Buffer=pdf}),r=>r.Url.EndsWith("/documents/"+doc.Id+"/pdf"));Assert.True(attached.Ok);
        await tools.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync();
        var form=page.Locator("app-query-form");await form.GetByLabel("Cuenta SRI").SelectOptionAsync(id);
        await form.GetByRole(AriaRole.Button,new(){Name="Seleccionar todos",Exact=true}).ClickAsync();await Assertions.Expect(form.Locator("legend")).ToContainTextAsync("6 seleccionados");
        await form.GetByRole(AriaRole.Button,new(){Name="Deseleccionar todos",Exact=true}).ClickAsync();await Assertions.Expect(form.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true})).ToBeDisabledAsync();
        await form.GetByLabel("Facturas",new(){Exact=true}).CheckAsync();await form.GetByLabel("Retenciones",new(){Exact=true}).CheckAsync();
        await form.GetByText("Opciones adicionales",new(){Exact=true}).ClickAsync();await form.GetByLabel("Operación").SelectOptionAsync("Stored");
        var queued=await page.RunAndWaitForResponseAsync(()=>form.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync(),r=>r.Url.EndsWith("/executions")&&r.Request.Method=="POST");Assert.True(queued.Ok);
        var job=JsonSerializer.Deserialize<Execution>(await queued.TextAsync(),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;Assert.Equal(2,job.Query.Types.Length);Assert.Equal(actor,job.RequestedBy);
        await Assertions.Expect(page.Locator("sri-consulta .badge[data-status='Completed']")).ToBeVisibleAsync(new(){Timeout=30000});
        await tools.GetByRole(AriaRole.Button,new(){Name="Generar / Actualizar Excel",Exact=true}).ClickAsync();await page.Locator("sri-reportes").WaitForAsync();await page.GetByLabel("Cuenta").SelectOptionAsync(id);await page.GetByLabel("Mes inicial").FillAsync("2026-10");
        await page.GetByRole(AriaRole.Button,new(){Name="Generar / actualizar mes",Exact=true}).ClickAsync();
        var download=await page.RunAndWaitForDownloadAsync(()=>page.GetByRole(AriaRole.Link,new(){Name="Descargar Excel 2026-10",Exact=true}).ClickAsync());Assert.Null(await download.FailureAsync());
        var artifacts=Path.Combine(root,"test-artifacts");Directory.CreateDirectory(artifacts);var report=Path.Combine(artifacts,"simplified-qa.xlsx");await download.SaveAsAsync(report);
        using(var book=new XLWorkbook(report)){Assert.Equal(3,book.Worksheets.Count);Assert.Equal("0012345678",book.Worksheet("Documentos").Cell(2,5).GetString());Assert.Equal(11.5m,book.Worksheet("Documentos").Cell(2,8).GetValue<decimal>());}
        var output=File.ReadLines(Path.Combine(root,".env")).Single(l=>l.StartsWith("SRI_OUTPUT_HOST_PATH=")).Split('=',2)[1].Trim('"','\'');Assert.Equal(xml,File.ReadAllBytes(Path.Combine(output,doc.XmlPath!)));Assert.Equal(pdf,File.ReadAllBytes(Path.Combine(output,doc.XmlPath!.Replace(".xml",".pdf"))));
        await page.ScreenshotAsync(new(){Path=Path.Combine(artifacts,"comprobantes-unificados.png"),FullPage=true});
        await tools.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync();form=page.Locator("app-query-form");await form.GetByLabel("Cuenta SRI").SelectOptionAsync(id);
        await form.GetByRole(AriaRole.Button,new(){Name="Descargar comprobantes",Exact=true}).ClickAsync();
        await Assertions.Expect(page.Locator("sri-consulta .badge[data-status='RequiresHumanAction']")).ToBeVisibleAsync(new(){Timeout=30000});await page.GetByRole(AriaRole.Button,new(){Name="Cancelar ejecución",Exact=true}).ClickAsync();await Assertions.Expect(page.Locator("sri-consulta .badge[data-status='Cancelled']")).ToBeVisibleAsync();
        await Nav("Historial");var accountAudit=page.Locator("tr[data-account='"+id+"']");await Assertions.Expect(accountAudit.GetByRole(AriaRole.Cell,new(){Name="Registro de cuenta SRI",Exact=true})).ToBeVisibleAsync();await Assertions.Expect(accountAudit.GetByRole(AriaRole.Cell,new(){Name="Generación de Excel",Exact=true})).ToBeVisibleAsync();
        var audit=await context.APIRequest.GetAsync("http://localhost:3000/api/v1/audit");var events=(await audit.JsonAsync())!.Value.GetProperty("items").EnumerateArray().Where(a=>a.GetProperty("account").ValueKind==JsonValueKind.String&&a.GetProperty("account").GetString()==id).ToList();Assert.Contains(events,a=>a.GetProperty("user").GetString()==actor&&a.GetProperty("action").GetString()=="Generación de Excel");Assert.DoesNotContain(dummy,await audit.TextAsync());
        await page.ScreenshotAsync(new(){Path=Path.Combine(artifacts,"auditoria-usuarios.png"),FullPage=true});
        await Nav("Configuración");await page.GetByLabel("Apariencia").SelectOptionAsync("dark");await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme","dark");if(await page.GetByRole(AriaRole.Button,new(){Name="Cerrar aviso",Exact=true}).IsVisibleAsync())await page.GetByRole(AriaRole.Button,new(){Name="Cerrar aviso",Exact=true}).ClickAsync();await page.SetViewportSizeAsync(390,844);await page.ScreenshotAsync(new(){Path=Path.Combine(artifacts,"configuracion-oscura-mobile.png"),FullPage=true});await page.SetViewportSizeAsync(1440,1000);
        var originalDefault=original.GetProperty("defaultAccount");if(originalDefault.ValueKind==JsonValueKind.String)await page.GetByLabel("Cuenta predeterminada").SelectOptionAsync(originalDefault.GetString()!);else await page.GetByLabel("Cuenta predeterminada").SelectOptionAsync(new SelectOptionValue{Label="Sin predeterminada"});
        await page.GetByRole(AriaRole.Button,new(){Name="Guardar preferencias",Exact=true}).ClickAsync();
        Assert.Equal(0,realConnections);Assert.Empty(errors);
        await File.WriteAllTextAsync(Path.Combine(artifacts,"frontend-operations-result.json"),JsonSerializer.Serialize(new{account=id,execution=job.Id,mode="Stored",status="Completed",xml=true,pdf=true,excel=true,realSri="Pending",initiated="Angular",sections=4,fictitiousCredentialEncryptedAndDeleted=true,audit=true},new JsonSerializerOptions{WriteIndented=true}));
        } finally {
            await context.APIRequest.PutAsync("http://localhost:3000/api/v1/settings",new(){Data=original.GetRawText(),Headers=new Dictionary<string,string>{{"Content-Type","application/json"}}});
        }
        async Task Nav(string name){await page.GetByRole(AriaRole.Link,new(){Name=name,Exact=true}).ClickAsync();await page.GetByRole(AriaRole.Heading,new(){Name=name,Exact=true}).WaitForAsync();}
    }
    private static string FindRoot(){for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,".env")))return d.FullName;throw new InvalidOperationException("Proyecto local no encontrado.");}
}
