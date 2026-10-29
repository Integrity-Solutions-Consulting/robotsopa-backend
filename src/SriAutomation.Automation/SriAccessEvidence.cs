using System.Text.RegularExpressions;
using Microsoft.Playwright;
namespace SriAutomation.Automation;

public sealed record AccessCheck(bool Verified,string Code);
public static class SriAccessEvidence
{
    public const string LoginUrl="https://srienlinea.sri.gob.ec/sri-en-linea/inicio/NAT";
    public static async Task<bool> Matches(IPage page,string taxId,bool fixture=false)=>(await Inspect(page,taxId,fixture)).Verified;
    public static async Task<AccessCheck> Inspect(IPage page,string taxId,bool fixture=false)
    {
        if(!Regex.IsMatch(taxId,@"^\d{13}$"))return new(false,"InvalidAccount");
        if(!fixture&&!await InteractiveNavigation.IsOfficialDocument(page))return new(false,"UnofficialOrigin");
        if(await HumanChallenge(page))return new(false,"HumanChallenge");
        if(!fixture&&new Uri(page.Url).AbsolutePath.StartsWith("/auth/",StringComparison.OrdinalIgnoreCase)||await page.Locator("input[type=password]:visible").CountAsync()!=0)return new(false,"LoginRequired");
        var text=await page.Locator("body").InnerTextAsync();
        if(Regex.IsMatch(text,@"\bSolicitud incorrecta\b",RegexOptions.IgnoreCase))return new(false,"SsoRequestRejected");
        if(!Regex.IsMatch(text,@"(?<!\d)"+Regex.Escape(taxId)+@"(?!\d)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))return new(false,"IdentityMismatch");
        // Observed official account page (including an icon-only toolbar). No form values count as identity.
        if(!fixture&&new Uri(page.Url).AbsolutePath.TrimEnd('/').Equals("/sri-en-linea/contribuyente/perfil",StringComparison.OrdinalIgnoreCase))return new(true,"Verified");
        var verified=await HasLogout(page);
        return new(verified,verified?"Verified":"UnrecognizedSession");
    }
    public static async Task<bool> HasLogout(IPage page)
    {
        var logout=page.GetByRole(AriaRole.Link,new(){NameRegex=new Regex(@"^(cerrar sesi[oó]n|salir|log\s*out)$",RegexOptions.IgnoreCase)})
            .Or(page.GetByRole(AriaRole.Button,new(){NameRegex=new Regex(@"^(cerrar sesi[oó]n|salir|log\s*out)$",RegexOptions.IgnoreCase)}));
        foreach(var control in await logout.AllAsync())if(await control.IsVisibleAsync())return true;
        // Inspect the attributes of actual controls, including accessible titles and logout icons.
        var icon=await page.Locator("a,button,[role=button]").EvaluateAllAsync<bool>("es=>es.some(e=>e.getClientRects().length&&/cerrar sesi[oó]n|logout|log.?out|power-off|sign-out/i.test([e.getAttribute('title'),e.getAttribute('aria-label'),e.getAttribute('href'),e.getAttribute('onclick'),e.className,...Array.from(e.querySelectorAll('i,span,svg')).map(x=>x.getAttribute('class'))].join(' ')))");
        return icon;
    }
    public static async Task<bool> HumanChallenge(IPage page)=>await page.Locator("iframe[title*=captcha i]:visible,input[name*=captcha i]:visible,[data-sitekey]:visible,input[autocomplete=one-time-code]:visible").CountAsync()>0;
}
