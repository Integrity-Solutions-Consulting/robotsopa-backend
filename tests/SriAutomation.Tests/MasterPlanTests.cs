using Microsoft.Playwright;
using SriAutomation.Automation;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;
public sealed class MasterPlanTests
{
    [Fact]
    public void CustomScheduleRespectsGuayaquilAndRejectsInvalidIntervals()
    {
        var first=new DateTimeOffset(2026,10,12,23,0,0,TimeSpan.FromHours(-5));
        Assert.Equal(first.AddDays(3),SriAutomation.Application.Validation.Next(first,"Custom",3));
        Assert.Equal(TimeSpan.FromHours(-5),SriAutomation.Application.Validation.Next(first,"Custom",3).Offset);
        Assert.Throws<ArgumentException>(()=>SriAutomation.Application.Validation.Next(first,"Custom",0));
        Assert.Throws<ArgumentException>(()=>SriAutomation.Application.Validation.Next(first,"Custom",366));
    }
    [Fact]
    public void RepeatedReadyCannotInvalidateAnAccessAlreadyUsedByTheBot()
    {
        var s=new SriAutomation.Domain.State{Accounts=[new("fixture","Ficticia","1790000000001")]};
        s.Authentications.Add(new(){Account="fixture",Status="Authenticated",BrowserOpened=true,SessionVerified=true,Command="Run"});
        var op=SriAutomation.Application.AuthenticationFlow.Command(s,"fixture","Verify");Assert.Equal("Run",op.Command);Assert.True(op.SessionVerified);
        Assert.Throws<ArgumentException>(()=>SriAutomation.Application.AuthenticationFlow.Command(s,"fixture","Reload"));
    }
    [BrowserFact]
    public async Task SsoCanReplaceTheVerifiedTabWithoutLosingTheAuthorizedQueryContext()
    {
        using var p=await Playwright.CreateAsync();await using var browser=await p.Chromium.LaunchAsync(new(){Headless=true});var context=await browser.NewContextAsync();
        await context.RouteAsync("https://srienlinea.sri.gob.ec/**",r=>r.FulfillAsync(new(){ContentType=r.Request.Url.Contains("/rest/menu/")?"application/json":"text/html",Body=r.Request.Url.Contains("/rest/menu/")?ProfileAccessTests.Menu:"<p>1790000000001</p><button>Salir</button>"}));
        var original=await context.NewPageAsync();await original.GotoAsync("https://srienlinea.sri.gob.ec/sri-en-linea/contribuyente/perfil");var bound=await BoundSriSession.Verify(original,"1790000000001");
        var replacement=await context.NewPageAsync();
        // about:blank is a transient tab, not an authentication failure.
        var transition=Task.Run(async()=>{await Task.Delay(400);await replacement.GotoAsync(SriPortal.PortalUrl);await replacement.SetContentAsync("<button>Salir</button><select><option>Factura</option></select>");await original.CloseAsync();});
        var selected=await AuthorizedQueryNavigation.Open(original,bound,"1790000000001",CancellationToken.None,5000);await transition;
        Assert.Same(replacement,selected);Assert.False(selected.IsClosed);Assert.True(original.IsClosed);
    }
}
