using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SriAutomation.Automation;

public static class AuthorizedQueryNavigation
{
    // An SSO bridge may replace or close its tab. The authenticated context, rather than
    // one captured IPage reference, owns the operation. Never submit query filters here.
    public static async Task<IPage> Open(IPage accountPage,BoundSriSession session,string taxId,CancellationToken cancellation,int timeoutMs=45000,Func<IPage,Task<bool>>? authenticate=null,Func<IPage,Task>? humanAction=null)
    {
        var context=accountPage.Context;var watch=Stopwatch.StartNew();var total=Stopwatch.StartNew();var clicked=new HashSet<string>();var submitted=new HashSet<IPage>();var notified=new HashSet<IPage>();
        var attempted=false;var reason="QueryNavigationFailed";
        while(watch.ElapsedMilliseconds<timeoutMs&&total.Elapsed<TimeSpan.FromMinutes(10)){
            cancellation.ThrowIfCancellationRequested();
            var waitingForHuman=false;
            foreach(var page in context.Pages.Reverse().Where(p=>!p.IsClosed).ToArray()){
                try{
                    if(!await InteractiveNavigation.IsOfficialDocument(page))continue;
                    var check=await session.Inspect(page,taxId);
                    if(!check.Verified){
                        reason=check.Code=="LoginRequired"&&submitted.Contains(page)?"LoginNotConfirmed":check.Code;
                        if(check.Code=="SsoRequestRejected")throw new PortalRecognitionException(check.Code);
                        if(check.Code=="HumanChallenge"){
                            if(humanAction is null)throw new PortalRecognitionException(check.Code);
                            waitingForHuman=true;if(notified.Add(page))await humanAction(page);continue;
                        }
                        if(check.Code=="LoginRequired"&&authenticate is not null&&submitted.Add(page)){
                            if(!await authenticate(page))throw new PortalRecognitionException("AutomaticLoginUnavailable");
                            reason="LoginNotConfirmed";
                        }
                        continue;
                    }
                    if(await ObservedPortalProfile.HasTypeControl(page))return page;
                    foreach(var name in new[]{@"^(consulta de )?(comprobantes (electr[oó]nicos )?)?emitidos$"}){
                        var action=page.GetByRole(AriaRole.Link,new(){NameRegex=new Regex(name,RegexOptions.IgnoreCase)}).Or(page.GetByRole(AriaRole.Button,new(){NameRegex=new Regex(name,RegexOptions.IgnoreCase)}));
                        var visible=new List<ILocator>();foreach(var c in await action.AllAsync())if(await c.IsVisibleAsync())visible.Add(c);
                        // URL is used only in memory for deduplication, never persisted.
                        var key=page.GetHashCode()+"|"+page.Url+"|"+name;
                        if(visible.Count==1&&clicked.Add(key)){await visible[0].ClickAsync(new(){Timeout=5000});attempted=true;}
                    }
                }catch(PlaywrightException)when(!cancellation.IsCancellationRequested){/* SSO document may be replaced. Observe current context again. */}
            }
            if(!attempted){
                attempted=true;
                var source=context.Pages.FirstOrDefault(p=>!p.IsClosed&&Uri.TryCreate(p.Url,UriKind.Absolute,out var u)&&u.Host=="srienlinea.sri.gob.ec");
                if(source is null){await Task.Delay(300,cancellation);attempted=false;continue;}
                var entry=await ProductionMenuEntry.Resolve(source);
                var query=await context.NewPageAsync();
                try{await query.GotoAsync(entry,new(){WaitUntil=WaitUntilState.Commit,Timeout=20000});}
                catch(PlaywrightException)when(!cancellation.IsCancellationRequested){/* Retain the original authorized account tab. */}
                catch(TimeoutException){reason="QueryNavigationFailed";}
            }
            if(waitingForHuman)watch.Stop();else watch.Start();
            await Task.Delay(300,cancellation);
        }
        throw new PortalRecognitionException(AccessCode(reason));
    }
    private static string AccessCode(string code)=>code is "HumanChallenge" or "LoginRequired" or "LoginNotConfirmed" or "IdentityMismatch" or "QueryAccountMissing"?code:"QueryNavigationFailed";
}
