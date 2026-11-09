using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SriAutomation.Automation;

// Resolve the production entry from SRI's published menu, rather than constructing SSO tokens.
public static class ProductionMenuEntry
{
    public const string MenuUrl="https://srienlinea.sri.gob.ec/sri-menu-portal-servicio-internet/rest/menu/porTipo?tipo=NAT";
    private static string Normal(string value)=>Regex.Replace(value.Normalize(System.Text.NormalizationForm.FormD),@"\p{Mn}","").Trim().ToLowerInvariant();
    public static string Resolve(JsonElement menu,Action<string>? diagnostic=null)
    {
        JsonElement Child(JsonElement nodes,string name){
            var matches=nodes.EnumerateArray().Where(n=>n.TryGetProperty("nombre",out var label)&&Normal(label.GetString()??"")==name).ToArray();
            diagnostic?.Invoke("MenuNode name="+(name=="facturacion electronica"?"billing":name=="produccion"?"production":"queries")+" matches="+(matches.Length==0?"zero":matches.Length==1?"one":"multiple"));
            if(matches.Length!=1)throw new PortalRecognitionException("QueryNavigationFailed");return matches[0];
        }
        var billing=Child(menu,"facturacion electronica");
        var production=Child(billing.GetProperty("items"),"produccion");
        var query=Child(production.GetProperty("items"),"consultas");
        var value=query.GetProperty("urlDestino").GetString();
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="srienlinea.sri.gob.ec"||!uri.IsDefaultPort||uri.UserInfo!=""||uri.AbsolutePath!="/tuportal-internet/accederAplicacion.jspa"||!Regex.IsMatch(uri.Query,@"^\?(redireccion=\d+&idGrupo=\d+|idGrupo=\d+&redireccion=\d+)$")){diagnostic?.Invoke("MenuEntry rejected");throw new PortalRecognitionException("QueryNavigationFailed");}
        diagnostic?.Invoke("MenuEntry accepted");
        return uri.AbsoluteUri;
    }
    public static async Task<string> Resolve(IPage page,Action<string>? diagnostic=null)
    {
        if(!await InteractiveNavigation.IsOfficialDocument(page))throw new PortalRecognitionException("UnofficialOrigin");
        try{
            diagnostic?.Invoke("MenuFetch started");
            var menu=await page.EvaluateAsync<JsonElement>("async url=>{const r=await fetch(url,{credentials:'omit',signal:AbortSignal.timeout(15000)});if(!r.ok)throw new Error('MenuUnavailable');return await r.json()}",MenuUrl);
            diagnostic?.Invoke("MenuFetch json-received");
            return Resolve(menu,diagnostic);
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or PlaywrightException){
            var kind=ex is PlaywrightException?BrowserErrorCategory(ex.Message):ex is JsonException?"json":ex is KeyNotFoundException?"missing-property":"invalid-structure";
            diagnostic?.Invoke("MenuResolution error="+kind);throw new PortalRecognitionException("QueryNavigationFailed");
        }
    }
    private static string BrowserErrorCategory(string message)=>message.Contains("MenuUnavailable",StringComparison.Ordinal)?"http-non-success":message.Contains("TimeoutError",StringComparison.Ordinal)?"fetch-timeout":message.Contains("SyntaxError",StringComparison.Ordinal)?"invalid-json":message.Contains("TypeError",StringComparison.Ordinal)?"fetch-or-type-error":"browser-error";
}
