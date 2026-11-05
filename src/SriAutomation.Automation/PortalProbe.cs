using System.Text.Json;
using Microsoft.Playwright;

namespace SriAutomation.Automation;

public static class PortalProbe
{
    public static async Task Run(string output)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel=OperatingSystem.IsWindows()?"msedge":null });
        var page = await browser.NewPageAsync();
        try {
        await page.GotoAsync(SriAccessEvidence.LoginUrl, new() { WaitUntil = WaitUntilState.Commit, Timeout = 45000 });
        await page.WaitForTimeoutAsync(6000);
        var controls = await page.Locator("input, button, select, form, a").EvaluateAllAsync<JsonElement>("elements => elements.map(e => ({tag:e.tagName,id:e.id,name:e.name,type:e.type,title:e.getAttribute('title'),aria:e.getAttribute('aria-label'),labels:e.labels ? Array.from(e.labels).map(l=>l.innerText) : [],text:['BUTTON','A'].includes(e.tagName) ? e.innerText : null,visible:!!e.getClientRects().length}))");
        // Solo estructura pública; no valores de inputs, URL de autenticación ni HTML.
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { inspectedAt = DateTimeOffset.UtcNow, origin = new Uri(page.Url).Host, controls }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Formulario público inspeccionado; no se introdujeron credenciales ni se consultaron comprobantes.");
        } catch(Exception ex) when(ex is TimeoutException or PlaywrightException) {
            await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new {inspectedAt=DateTimeOffset.UtcNow,status="Unavailable",reason=ex.GetType().Name,authenticated=false}));
            Console.WriteLine("El navegador local no pudo inspeccionar el formulario público. Evidencia guardada sin URL de autenticación ni secretos.");
        }
    }
    public static async Task CapturePrivateStructure(IPage page)
    {
        var structure = await page.Locator("input,select,button,form,table,a").EvaluateAllAsync<JsonElement>("elements => elements.filter(e=>!e.closest('tbody')).map(e=>({tag:e.tagName,id:e.id,name:e.name,type:e.type,role:e.getAttribute('role'),text:['BUTTON','A'].includes(e.tagName)?e.innerText.replace(/[0-9]{10,49}/g,'[identificacion]'):null,options:e.tagName==='SELECT'?Array.from(e.options).map(o=>({value:o.value,text:o.text.replace(/[0-9]{10,49}/g,'[identificacion]')})):null,headings:e.tagName==='TABLE'?Array.from(e.querySelectorAll('th')).map(h=>h.innerText):null}))");
        Directory.CreateDirectory(".secrets");
        await File.WriteAllTextAsync(".secrets/portal-structure.json", JsonSerializer.Serialize(structure, new JsonSerializerOptions { WriteIndented = true }));
    }
}
