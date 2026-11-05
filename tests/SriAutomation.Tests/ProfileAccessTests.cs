using Microsoft.Playwright;
using SriAutomation.Automation;
using SriAutomation.Infrastructure;
namespace SriAutomation.Tests;
public sealed class ProfileAccessTests
{
    internal const string Menu="""[{"nombre":"Facturación Electrónica","items":[{"nombre":"Producción","items":[{"nombre":"Consultas","urlDestino":"https://srienlinea.sri.gob.ec/tuportal-internet/accederAplicacion.jspa?redireccion=60&idGrupo=58"}]}]}]""";
    [Fact]
    public void ProductionEntryUsesPublishedMenuAndRejectsAnotherOriginOrFabricatedToken()
    {
        using var json=System.Text.Json.JsonDocument.Parse(Menu);
        Assert.Contains("accederAplicacion.jspa?redireccion=60&idGrupo=58",ProductionMenuEntry.Resolve(json.RootElement));
        foreach(var invalid in new[]{Menu.Replace("srienlinea.sri.gob.ec/tuportal", "example.com/tuportal"),Menu.Replace("accederAplicacion.jspa?redireccion=60&idGrupo=58","GeneraToken.jsp?token=ficticio"),Menu.Replace("Producción","Pruebas")}){
            using var other=System.Text.Json.JsonDocument.Parse(invalid);Assert.Throws<PortalRecognitionException>(()=>ProductionMenuEntry.Resolve(other.RootElement));
        }
    }
    [Fact]
    public async Task EncryptedProfileBindsAccountAndRucAndCanBeRotatedAndDeleted()
    {
        var root=Path.Combine(Path.GetTempPath(),"sri-profile-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var key=Path.Combine(root,"key");File.WriteAllBytes(key,System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));var vault=new CredentialVault(root,key);
        try{vault.Save("fixture","1790000000001","FICTICIA-SECRET");Assert.DoesNotContain("FICTICIA-SECRET",File.ReadAllText(Path.Combine(root,"fixture.credential")));await vault.Use("fixture","1790000000001",secret=>{Assert.Equal("FICTICIA-SECRET",secret);return Task.CompletedTask;});await Assert.ThrowsAsync<InvalidDataException>(()=>vault.Use("fixture","1790000000002",_=>Task.CompletedTask));File.Copy(Path.Combine(root,"fixture.credential"),Path.Combine(root,"other.credential"));await Assert.ThrowsAsync<InvalidDataException>(()=>vault.Use("other","1790000000001",_=>Task.CompletedTask));vault.Save("fixture","1790000000001","ROTADA");await vault.Use("fixture","1790000000001",secret=>{Assert.Equal("ROTADA",secret);return Task.CompletedTask;});vault.Delete("fixture");Assert.False(vault.Present("fixture"));}finally{Directory.Delete(root,true);}
    }
    private const string Form="<label for=u>RUC</label><input id=u name=username><label for=a>C.I. adicional</label><input id=a name=adicional><input type=password><button onclick='window.sent=(window.sent||0)+1'>Ingresar</button>";
    [BrowserFact]
    public async Task ObservedProductionFilterLabelsMapPluralAuthorizedAndVoucherTypes()
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();
        await page.SetContentAsync("""
        <label for=fecha>Fecha de emisión</label><input id=fecha>
        <select id=estado><option value=AUT>Autorizados</option><option value=NAT>No Autorizados</option></select>
        <select id=tipo><option value=1>Factura</option><option value=2>Liquidación de compra de bienes y prestación de servicios</option><option value=3>Notas de Crédito</option><option value=4>Notas de Débito</option><option value=5>Guías de Remisión</option><option value=6>Comprobante de Retención</option></select><button>Consultar</button>
        """);
        var profile=await ObservedPortalProfile.Discover(page,"1790000000001",true);Assert.Equal("AUT",profile.StatusValues["Autorizado"]);Assert.Equal(6,profile.TypeValues.Count);Assert.Equal("3",profile.TypeValues["Notas-Credito"]);Assert.Equal("4",profile.TypeValues["Notas-Debito"]);
        await page.Locator("#tipo").EvaluateAsync("e=>e.insertAdjacentHTML('beforeend','<option value=7>Comprobante de Retención versión anterior</option>')");
        profile=await ObservedPortalProfile.Discover(page,"1790000000001",true);Assert.Equal("1",profile.TypeValues["Facturas"]);Assert.False(profile.TypeValues.ContainsKey("Retenciones"));
    }
    [BrowserFact]
    public async Task LegacyProductionMenuNeedsBoundContextExactAccountAndObservedQueryForm()
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();
        await page.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body="<p>1790000000001</p><form id=consultaDocumentoForm></form>"}));
        await page.GotoAsync("https://srienlinea.sri.gob.ec/sri-en-linea/contribuyente/perfil");var bound=await BoundSriSession.Verify(page,"1790000000001");
        await page.GotoAsync(SriPortal.PortalUrl);await page.Locator("form").EvaluateAsync("e=>e.style.minHeight='20px'");
        Assert.False((await SriAccessEvidence.Inspect(page,"1790000000001")).Verified);Assert.True((await bound.Inspect(page,"1790000000001")).Verified);
        Assert.False((await bound.Inspect(page,"1790000000002")).Verified);await page.SetContentAsync("<p>1790000000002</p><form id=consultaDocumentoForm style='height:20px'></form>");Assert.False((await bound.Inspect(page,"1790000000001")).Verified);
    }
    [BrowserFact]
    public async Task RejectedSsoCannotCountAsAuthenticatedEvenWithAnAccountAndLogoutIcon()
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();
        await page.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body="<p>1790000000001</p><button>Salir</button><h1>Error</h1><p>Solicitud incorrecta</p>"}));
        await page.GotoAsync("https://srienlinea.sri.gob.ec/tuportal-internet/GeneraToken.jsp");
        var check=await SriAccessEvidence.Inspect(page,"1790000000001");Assert.False(check.Verified);Assert.Equal("SsoRequestRejected",check.Code);
    }
    [BrowserFact]
    public async Task LoginRejectsWrongOriginAmbiguityAndHumanChallenge()
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();await page.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body=Form}));await page.GotoAsync("https://srienlinea.sri.gob.ec/auth/fixture");Assert.True(await AuthorizedLogin.Submit(page,"1790000000001","FICTICIA"));Assert.Equal(1,await page.EvaluateAsync<int>("window.sent"));Assert.Equal("",await page.Locator("#a").InputValueAsync());await page.SetContentAsync(Form+"<input autocomplete=one-time-code>");Assert.False(await AuthorizedLogin.Submit(page,"1790000000001","FICTICIA"));Assert.Equal("",await page.Locator("input[type=password]").InputValueAsync());await page.SetContentAsync(Form+"<input aria-label=RUC>");Assert.False(await AuthorizedLogin.Submit(page,"1790000000001","FICTICIA"));await page.GotoAsync("about:blank");await page.SetContentAsync(Form);Assert.False(await AuthorizedLogin.Submit(page,"1790000000001","FICTICIA"));
    }
    [BrowserTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task QueryApplicationCanRequireItsOwnLoginAndRejectionDoesNotResubmit(bool rejected)
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var context=await browser.NewContextAsync();
        await context.RouteAsync("https://srienlinea.sri.gob.ec/**",async r=>{
            var path=new Uri(r.Request.Url).AbsolutePath;
            if(path.Contains("/rest/menu/")){await r.FulfillAsync(new(){ContentType="application/json",Body=Menu});return;}
            if(path.EndsWith("accederAplicacion.jspa")){await r.FulfillAsync(new(){ContentType="text/html",Body="<script>location.replace('/auth/second')</script>"});return;}
            var body=path.StartsWith("/auth/")?rejected?Form:Form.Replace("window.sent=(window.sent||0)+1","location.href=\"/comprobantes-electronicos-internet/ready\""):path.EndsWith("/ready")?"<button>Salir</button><select><option>Factura</option></select>":"<p>1790000000001</p><button>Salir</button>";
            await r.FulfillAsync(new(){ContentType="text/html",Body=body});
        });
        var original=await context.NewPageAsync();await original.GotoAsync("https://srienlinea.sri.gob.ec/sri-en-linea/contribuyente/perfil");var bound=await BoundSriSession.Verify(original,"1790000000001");var calls=0;
        async Task<IPage> Run()=>await AuthorizedQueryNavigation.Open(original,bound,"1790000000001",CancellationToken.None,rejected?1800:7000,async page=>{calls++;return await AuthorizedLogin.Submit(page,"1790000000001","FICTICIA");});
        if(rejected){var error=await Assert.ThrowsAsync<PortalRecognitionException>(Run);Assert.Equal("LoginNotConfirmed",error.Code);}else{var result=await Run();Assert.EndsWith("/ready",result.Url);Assert.False(original.IsClosed);}
        Assert.Equal(1,calls);
    }
    [BrowserFact]
    public async Task HumanChallengePausesNavigationWithoutSubmittingCredentialsOrFailingTheJob()
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var context=await browser.NewContextAsync();await context.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType=r.Request.Url.Contains("/rest/menu/")?"application/json":"text/html",Body=r.Request.Url.Contains("/rest/menu/")?Menu:r.Request.Url.Contains("accederAplicacion.jspa")?"<input autocomplete=one-time-code>":"<p>1790000000001</p><button>Salir</button>"}));var original=await context.NewPageAsync();await original.GotoAsync("https://srienlinea.sri.gob.ec/sri-en-linea/contribuyente/perfil");var bound=await BoundSriSession.Verify(original,"1790000000001");var prompts=0;var submissions=0;
        var result=await AuthorizedQueryNavigation.Open(original,bound,"1790000000001",CancellationToken.None,5000,_=>{submissions++;return Task.FromResult(false);},async page=>{prompts++;await page.SetContentAsync("<button>Salir</button><select><option>Factura</option></select>");});Assert.Equal(1,prompts);Assert.Equal(0,submissions);Assert.False(result.IsClosed);
    }
}
