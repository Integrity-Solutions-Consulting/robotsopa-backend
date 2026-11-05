using System.Text.RegularExpressions;
using Microsoft.Playwright;
namespace SriAutomation.Automation;
public static class AuthorizedLogin
{
    public static async Task<bool> Submit(IPage page,string taxId,string password)
    {
        if(!await InteractiveNavigation.IsOfficialDocument(page)||!new Uri(page.Url).AbsolutePath.StartsWith("/auth/",StringComparison.OrdinalIgnoreCase)||await SriAccessEvidence.HumanChallenge(page))return false;
        var passwords=page.Locator("input[type=password]:visible");if(await passwords.CountAsync()!=1)return false;
        var users=new List<ILocator>();foreach(var c in await page.Locator("input:not([type=password]):not([type=hidden]):not([type=submit]):visible").AllAsync()){
            var description=await c.EvaluateAsync<string>("e=>[e.id,e.name,e.getAttribute('aria-label'),...(e.labels?Array.from(e.labels).map(l=>l.innerText):[])].join(' ')");
            if(!Regex.IsMatch(description,"adicional|additional",RegexOptions.IgnoreCase)&&Regex.IsMatch(description,@"\b(ruc|usuario|username|pasaporte)\b",RegexOptions.IgnoreCase))users.Add(c);
        }
        var actions=new List<ILocator>();foreach(var c in await page.GetByRole(AriaRole.Button,new(){NameRegex=new Regex(@"^(ingresar|iniciar sesi[oó]n|acceder|entrar|login)$",RegexOptions.IgnoreCase)}).AllAsync())if(await c.IsVisibleAsync())actions.Add(c);
        if(users.Count!=1||actions.Count!=1||!await InteractiveNavigation.IsOfficialDocument(page))return false;
        await users[0].FillAsync(taxId);
        if(!await InteractiveNavigation.IsOfficialDocument(page))return false;
        await passwords.FillAsync(password);
        if(!await InteractiveNavigation.IsOfficialDocument(page)||await SriAccessEvidence.HumanChallenge(page))return false;
        await actions[0].ClickAsync();return true;
    }
}
