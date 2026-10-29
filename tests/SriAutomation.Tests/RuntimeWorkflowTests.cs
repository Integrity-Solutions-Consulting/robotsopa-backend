using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
namespace SriAutomation.Tests;
public sealed class RuntimeWorkflowTests
{
    [BrowserFact]
    public async Task QueryWithoutRucRequiresPreviouslyVerifiedContextAndVisibleLogout()
    {
        using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});
        var page=await browser.NewPageAsync();
        await page.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body="<p>1790000000001</p><button>Salir</button>"}));
        await page.GotoAsync("https://srienlinea.sri.gob.ec/sri-en-linea/contribuyente/perfil");
        var bound=await BoundSriSession.Verify(page,"1790000000001");
        await page.SetContentAsync("<p>1790000000002</p><button>Salir</button>");Assert.Equal("IdentityMismatch",(await bound.Inspect(page,"1790000000001")).Code);
        await page.GotoAsync(SriPortal.PortalUrl);await page.SetContentAsync("<button>Salir</button><select><option>Factura</option></select>");
        Assert.False(await SriAccessEvidence.Matches(page,"1790000000001"));
        Assert.True((await bound.Inspect(page,"1790000000001")).Verified);
        Assert.False((await bound.Inspect(page,"1790000000002")).Verified);
        var other=await browser.NewPageAsync();Assert.False((await bound.Inspect(other,"1790000000001")).Verified);
        await page.SetContentAsync("<button>Salir</button><input type=password>");Assert.Equal("LoginRequired",(await bound.Inspect(page,"1790000000001")).Code);
        await page.SetContentAsync("<select><option>Factura</option></select>");Assert.Equal("QueryAccountMissing",(await bound.Inspect(page,"1790000000001")).Code);
        await page.SetContentAsync("<button>Salir</button><input autocomplete=one-time-code>");Assert.Equal("HumanChallenge",(await bound.Inspect(page,"1790000000001")).Code);
    }
    [BrowserFact]
    public async Task RecognitionReportsWhichControlIsMissingWithoutSendingQuery()
    {
        using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();
        await page.GotoAsync("about:blank");
        await page.SetContentAsync("<select><option>Factura</option></select><button>Consultar</button>");
        var date=await Assert.ThrowsAsync<PortalRecognitionException>(()=>ObservedPortalProfile.Discover(page,"1790000000001",true));Assert.Equal("QueryDateMissing",date.Code);
        await page.SetContentAsync("<select><option>Factura</option></select><label for=fecha>Fecha</label><input id=fecha>");
        var search=await Assert.ThrowsAsync<PortalRecognitionException>(()=>ObservedPortalProfile.Discover(page,"1790000000001",true));Assert.Equal("QuerySearchMissing",search.Code);
        await page.SetContentAsync("<select><option>Todos</option></select><button>Consultar</button>");
        var type=await Assert.ThrowsAsync<PortalRecognitionException>(()=>ObservedPortalProfile.Discover(page,"1790000000001",true));Assert.Equal("QueryTypeMissing",type.Code);
        Assert.NotNull(AccessMessages.For(date.Code));Assert.NotNull(AccessMessages.For(search.Code));Assert.NotNull(AccessMessages.For(type.Code));
    }
    [BrowserFact]
    public async Task OfficialAccountPageAndIconOnlyLogoutAreRecognizedWithoutTrustingLoginValues()
    {
        using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();
        // The official-origin responses are intercepted locally. No SRI request is sent.
        await page.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body="<body><p>Cuenta 1790000000001</p><button><i class='power-off'></i></button></body>"}));
        await page.GotoAsync("https://srienlinea.sri.gob.ec/sri-en-linea/contribuyente/perfil?faces-redirect=true");
        Assert.True(await SriAccessEvidence.Matches(page,"1790000000001"));
        Assert.False(await SriAccessEvidence.Matches(page,"1790000000002"));
        await page.GotoAsync("https://srienlinea.sri.gob.ec/consulta");Assert.True(await SriAccessEvidence.Matches(page,"1790000000001"));
        await page.SetContentAsync("<p>Cuenta 1790000000001</p><input type='password'><button>Salir</button>");
        Assert.Equal("LoginRequired",(await SriAccessEvidence.Inspect(page,"1790000000001")).Code);
        await page.SetContentAsync("<input value='1790000000001'><button>Salir</button>");Assert.False(await SriAccessEvidence.Matches(page,"1790000000001"));
    }
    [BrowserTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task DiscoversActualControlsSearchesPaginatesAndDownloadsWithoutManualConfiguration(bool empty)
    {
        const string html="""
        <html><head><meta charset="utf-8"></head><body><p>Cuenta ficticia 1790000000001</p><button>Salir</button>
        <label for="runtime-date">Fecha de emisión</label><input id="runtime-date">
        <label for="runtime-types">Comprobante</label><select id="runtime-types"><option value="01">Factura</option></select>
        <button onclick="current=0;load()">Consultar</button><div id="empty" hidden>Sin documentos</div>
        <table><thead><tr><th>Clave de acceso</th><th>Fecha de emisión</th><th>Documentos</th></tr></thead><tbody></tbody></table>
        <button onclick="current++;load()">Siguiente</button>
        <script>let current=0;async function load(){let data=await(await fetch('/query',{method:'POST',body:JSON.stringify({page:current,date:document.querySelector('#runtime-date').value})})).json();document.querySelector('tbody').innerHTML=data.keys.map(k=>`<tr><td>${k}</td><td>12/09/2026</td><td><a href='/files/${k}.xml' download>XML</a><a href='/files/${k}.pdf' download>PDF</a></td></tr>`).join('');document.querySelector('#empty').hidden=data.keys.length>0;document.querySelector('button:last-of-type').disabled=!data.next}</script></body></html>
        """;
        await using var server=await FixtureServer.Start(html,empty);using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();await page.GotoAsync(server.Url);
        var profile=await ObservedPortalProfile.Discover(page,"1790000000001",true);profile.Validate(true);Assert.Equal(0,server.Queries);
        var portal=new SriQueryPage(page,profile);await portal.Search(new("qa",new(2026,9,12),new(2026,9,12),["Facturas"]),new(2026,9,12),"Facturas",CancellationToken.None);
        var rows=await portal.Rows();Assert.Equal(empty?0:1,rows.Count);
        if(empty){Assert.False(await portal.Next(CancellationToken.None));Assert.Equal(0,server.Files);return;}
        Assert.Equal(PhaseTwoTests.Key,rows[0].Key);Assert.Equal(new DateOnly(2026,9,12),rows[0].Issued);
        Assert.NotEmpty((await portal.Download(rows[0],"xml",CancellationToken.None))!);Assert.NotEmpty((await portal.Download(rows[0],"pdf",CancellationToken.None))!);
        Assert.True(await portal.Next(CancellationToken.None));Assert.NotEqual(rows[0].Key,(await portal.Rows())[0].Key);Assert.False(await portal.Next(CancellationToken.None));Assert.Equal(2,server.Queries);Assert.Equal(2,server.Files);
    }
    [Fact]
    public void NativeTransportCannotFinishAJobOrChangeItsQueryAndRejectsCancelledAccess()
    {
        var account=new Account("real-fixture","Ficticia","1790000000001");var state=new State{Accounts=[account]};var query=new Query(account.Id,new(2026,10,12),new(2026,10,12),["Facturas"]);var job=Validation.Enqueue(state,query);job.Status="Running";
        state.Authentications.Add(new(){Account=account.Id,ExecutionId=job.Id,SessionVerified=true,Expires=DateTimeOffset.UtcNow.AddMinutes(10)});
        var delta=new State{Executions=[new(){Id=job.Id,Query=query with{Types=["Retenciones"]},Status="Completed",Stage="Querying",Downloaded=1,AttemptedKeys=[PhaseTwoTests.Key]}]};
        NativeExecution.Apply(state,job.Id,delta);Assert.Equal("Running",job.Status);Assert.Equal(query with{Xml=false},job.Query);Assert.Equal("Querying",job.Stage);Assert.Single(job.Events);
        job.CancelRequested=true;Assert.Throws<ArgumentException>(()=>NativeExecution.Apply(state,job.Id,delta));
    }
    [Fact]
    public void NativeTransportAcceptsMoreThanTenDistinctDocuments()
    {
        var account=new Account("real-fixture","Ficticia","1790000000001");var state=new State{Accounts=[account]};
        var job=Validation.Enqueue(state,new(account.Id,new(2026,10,12),new(2026,10,12),["Facturas"]));job.Status="Running";
        state.Authentications.Add(new(){Account=account.Id,ExecutionId=job.Id,SessionVerified=true,Expires=DateTimeOffset.UtcNow.AddMinutes(10)});
        var keys=Enumerable.Range(1,25).Select(i=>i.ToString().PadLeft(49,'0')).ToList();
        NativeExecution.Apply(state,job.Id,new State{Executions=[new(){Id=job.Id,Query=job.Query,AttemptedKeys=keys}]});
        Assert.Equal(25,job.AttemptedKeys.Count);
    }
}
