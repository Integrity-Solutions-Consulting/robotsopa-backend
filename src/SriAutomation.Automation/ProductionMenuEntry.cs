using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SriAutomation.Automation;

// Resolve the production entry from SRI's published menu, rather than constructing SSO tokens.
public static class ProductionMenuEntry
{
    public const string MenuUrl="https://srienlinea.sri.gob.ec/sri-menu-portal-servicio-internet/rest/menu/porTipo?tipo=NAT";
    private static string Normal(string value)=>Regex.Replace(value.Normalize(System.Text.NormalizationForm.FormD),@"\p{Mn}","").Trim().ToLowerInvariant();
    public static string Resolve(JsonElement menu)
    {
        JsonElement Child(JsonElement nodes,string name){
            var matches=nodes.EnumerateArray().Where(n=>n.TryGetProperty("nombre",out var label)&&Normal(label.GetString()??"")==name).ToArray();
            if(matches.Length!=1)throw new PortalRecognitionException("QueryNavigationFailed");return matches[0];
        }
        var billing=Child(menu,"facturacion electronica");
        var production=Child(billing.GetProperty("items"),"produccion");
        var query=Child(production.GetProperty("items"),"consultas");
        var value=query.GetProperty("urlDestino").GetString();
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="srienlinea.sri.gob.ec"||!uri.IsDefaultPort||uri.UserInfo!=""||uri.AbsolutePath!="/tuportal-internet/accederAplicacion.jspa"||!Regex.IsMatch(uri.Query,@"^\?(redireccion=\d+&idGrupo=\d+|idGrupo=\d+&redireccion=\d+)$"))throw new PortalRecognitionException("QueryNavigationFailed");
        return uri.AbsoluteUri;
    }
    public static async Task<string> Resolve(IPage page)
    {
        if(!await InteractiveNavigation.IsOfficialDocument(page))throw new PortalRecognitionException("UnofficialOrigin");
        try{return Resolve(await page.EvaluateAsync<JsonElement>("async url=>{const r=await fetch(url,{credentials:'omit',signal:AbortSignal.timeout(15000)});if(!r.ok)throw new Error('MenuUnavailable');return await r.json()}",MenuUrl));}
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or PlaywrightException){throw new PortalRecognitionException("QueryNavigationFailed");}
    }
}
