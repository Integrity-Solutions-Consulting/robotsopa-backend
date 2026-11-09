using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
namespace SriAutomation.Tests;
public sealed class RuntimeWorkflowTests
{
    [BrowserFact]
    public async Task EmptyXmlColumnAndUnknownControlAreDistinct()
    {
        using var playwright=await Playwright.CreateAsync(); await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true, ExecutablePath=Environment.GetEnvironmentVariable("SRI_TEST_CHROMIUM")});
        var page=await browser.NewPageAsync();
        var profile=new PortalProfile{RuntimeObserved=true};
        await page.SetContentAsync("<table><thead><tr><th>XML</th><th>PDF</th></tr></thead><tbody><tr><td></td><td><button>PDF</button></td></tr></tbody></table>");
        var query=new SriQueryPage(page,profile); var row=new PortalRow(new string('1',49),new DateOnly(2026,1,1),page.Locator("tbody tr"));
        Assert.Equal(DownloadControlState.Unavailable,(await query.DownloadWithStatus(row,"xml",CancellationToken.None)).State);
        await page.Locator("tbody td").First.EvaluateAsync("e=>e.innerHTML='<button>?</button>'");
        Assert.Equal(DownloadControlState.Unrecognized,(await query.DownloadWithStatus(row,"xml",CancellationToken.None)).State);
    }
    [BrowserFact]
    public async Task FormatHeadersIdentifyOnlyUniqueControlsAndDiagnosticsExcludePrivateValues()
    {
        using var playwright=await Playwright.CreateAsync(); await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});
        var page=await browser.NewPageAsync();
        await page.SetContentAsync("<table><thead><tr><th>Clave</th><th>XML</th><th>PDF</th></tr></thead><tbody><tr><td>SECRET123</td><td><a id='x' href='/SECRET123'><i style='display:inline-block;width:16px;height:16px'></i></a></td><td><button id='p'><svg></svg></button></td></tr></tbody></table>");
        var row=page.Locator("tbody tr");
        Assert.Equal("x",await (await ObservedPortalProfile.DownloadControl(row,"xml"))!.GetAttributeAsync("id"));
        Assert.Equal("p",await (await ObservedPortalProfile.DownloadControl(row,"pdf"))!.GetAttributeAsync("id"));
        await row.Locator("td").Nth(2).EvaluateAsync("e=>e.insertAdjacentHTML('beforeend','<button>other</button>')");
        await Assert.ThrowsAsync<InvalidDataException>(()=>ObservedPortalProfile.DownloadControl(row,"pdf"));
        await page.SetContentAsync("<table><tbody><tr><td>SECRET123</td><td><button title='SECRET123' onclick='SECRET123()'><i></i></button></td></tr></tbody></table>");
        var events=new List<string>(); var profile=new PortalProfile{RuntimeObserved=true,TraceStep=events.Add};
        var result=await new SriQueryPage(page,profile).Download(new PortalRow(new string('1',49),new DateOnly(2026,1,1),page.Locator("tr")),"pdf",CancellationToken.None);
        Assert.Null(result); Assert.Contains(events,e=>e.Contains("ControlMissing")); Assert.DoesNotContain(events,e=>e.Contains("SECRET123"));
    }
    [BrowserFact]
    public async Task IconOnlyDownloadControlsAreIdentifiedWithoutGuessingAmbiguousControls()
    {
        using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<table><tbody><tr><td><button id='pdf'><span class='ui_icon_pdf'></span></button><button id='xml'><i class='file-xml'></i></button></td></tr></tbody></table>");
        var row = page.Locator("tr");
        var pdf = await ObservedPortalProfile.DownloadControl(row, "pdf");
        var xml = await ObservedPortalProfile.DownloadControl(row, "xml");
        Assert.NotNull(pdf); Assert.Equal("pdf", await pdf!.GetAttributeAsync("id"));
        Assert.NotNull(xml); Assert.Equal("xml", await xml!.GetAttributeAsync("id"));
        await row.Locator("td").EvaluateAsync("e => e.insertAdjacentHTML('beforeend', '<button><span class=icon-pdf></span></button>')");
        await Assert.ThrowsAsync<InvalidDataException>(() => ObservedPortalProfile.DownloadControl(row, "pdf"));
    }
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
        <script>let current=0;async function load(){let data=await(await fetch('/query',{method:'POST',body:JSON.stringify({page:current,date:document.querySelector('#runtime-date').value})})).json();document.querySelector('tbody').innerHTML=data.keys.map(k=>`<tr><td>${k}</td><td>12/09/2026</td><td><a href='/download/${k}/0' download><span class='icon_xml' style='display:inline-block;width:20px;height:20px'></span></a><a href='/download/${k}/1' download><span class='icon_pdf' style='display:inline-block;width:20px;height:20px'></span></a></td></tr>`).join('');document.querySelector('#empty').hidden=data.keys.length>0;document.querySelector('button:last-of-type').disabled=!data.next}</script></body></html>
        """;
        await using var server=await FixtureServer.Start(html,empty);using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();await page.GotoAsync(server.Url);
        var profile=await ObservedPortalProfile.Discover(page,"1790000000001",true);var trace=new List<string>();profile.TraceStep=trace.Add;profile.Validate(true);Assert.Equal(0,server.Queries);
        var portal=new SriQueryPage(page,profile);await portal.Search(new("qa",new(2026,9,12),new(2026,9,12),["Facturas"]),new(2026,9,12),"Facturas",CancellationToken.None);
        var rows=await portal.Rows();Assert.Equal(empty?0:1,rows.Count);
        if(empty){Assert.False(await portal.Next(CancellationToken.None));Assert.Equal(0,server.Files);return;}
        Assert.Equal(PhaseTwoTests.Key,rows[0].Key);Assert.Equal(new DateOnly(2026,9,12),rows[0].Issued);
        Assert.NotEmpty((await portal.Download(rows[0],"xml",CancellationToken.None))!);Assert.NotEmpty((await portal.Download(rows[0],"pdf",CancellationToken.None))!);
        Assert.Contains("DownloadPDFControlFound",trace);Assert.Contains("DownloadPDFClickAttempted",trace);Assert.Contains("DownloadPDFClickCompleted",trace);Assert.Contains("DownloadPDFReceived",trace);Assert.DoesNotContain(trace,e=>e.Contains(PhaseTwoTests.Key));
        Assert.True(await portal.Next(CancellationToken.None));Assert.NotEqual(rows[0].Key,(await portal.Rows())[0].Key);Assert.False(await portal.Next(CancellationToken.None));Assert.Equal(2,server.Queries);Assert.Equal(2,server.Files);
    }
}
