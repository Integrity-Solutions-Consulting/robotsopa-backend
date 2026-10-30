using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using SriAutomation.Automation;
using SriAutomation.Application;
using SriAutomation.LocalAgent;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../.."));
Directory.SetCurrentDirectory(root);
Directory.CreateDirectory(".secrets");
var secretRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SRI-Automation","secrets");
FileStream singleton;
try { singleton = new FileStream(".secrets/agent.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException) { return; }
using var agentLock = singleton;
Environment.SetEnvironmentVariable("SRI_SESSION_PATH", Path.Combine(root, "storage/sessions"));
Environment.SetEnvironmentVariable("SRI_SESSION_KEY_FILE", Path.Combine(secretRoot, "session.key"));
using var client = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("SRI_AGENT_API") ?? "http://127.0.0.1:5000"), Timeout = TimeSpan.FromSeconds(8) };
if (!client.BaseAddress.IsLoopback) throw new InvalidOperationException("El agente solo admite una API local.");
client.DefaultRequestHeaders.Add("X-SRI-Agent-Key", File.ReadAllText(Path.Combine(secretRoot,"agent.key")).Trim());
using var playwright = await Playwright.CreateAsync();
var interactive = new InteractiveAgent(playwright, client, root);
var registered = false;
_ = Task.Run(async () => {
    while (true) {
        try { using var heartbeat = await client.PostAsync("/api/internal/agent/heartbeat", null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        await Task.Delay(3000);
    }
});
// No se guardan excepciones Playwright: pueden incluir URL o contenido autenticado.
while (true)
{
    try
    {
        if (!registered) {
            using var recovered = await client.PostAsync("/api/internal/agent/recover", null);
            recovered.EnsureSuccessStatusCode(); registered = true;
        }
        using var response = await client.PostAsync("/api/internal/agent/poll", null);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
        {
            var command = await response.Content.ReadFromJsonAsync<AgentCommand>();
            if (command is not null) await interactive.Handle(command);
        }
        await interactive.CheckClosed();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException) { /* API reiniciándose: reintentar sin filtrar secretos. */ }
    await Task.Delay(250);
}

internal sealed record AgentCommand(string Operation, string Account, string? TaxId, string Command, bool CancelRequested,string? ExecutionId=null);
internal sealed class InteractiveAgent(IPlaywright playwright, HttpClient client, string root)
{
    private IBrowser? browser;
    private IPage? page;
    private AgentCommand? current;
    private Task? navigation;
    private int generation;
    private DateTimeOffset openedAt;
    private string lastObservation="";
    private bool portalLoaded;
    private bool accessConfirmed;
    private BoundSriSession? boundSession;
    private bool accessReported;
    private DateTimeOffset verifiedAt;
    private DateTimeOffset? credentialAttemptedAt;
    private bool queryPaused;
    private string pauseReason="QueryControlsMissing";
    private DateTimeOffset pausedAt;
    private CancellationTokenSource? runCancel;
    private Task? running;
    private readonly System.Collections.Concurrent.ConcurrentQueue<object> queryResponses=new();
    private Task Report(string status, bool opened) => Send(current!.Operation, status, opened);
    private async Task Send(string operation, string status, bool opened,string? reasonCode=null)
    {
        using var response = await client.PostAsJsonAsync("/api/internal/agent/report", new { operation, status, browserOpened = opened,reasonCode });
        response.EnsureSuccessStatusCode();
    }
    public async Task Handle(AgentCommand command)
    {
        try
        {
            if (command.Command == "RemoteLogout")
            {
                var confirmed = false;
                try {
                    var profile = PortalProfile.Load(Path.Combine(root,"config/sri-portal.json")); profile.Validate();
                    if (string.IsNullOrWhiteSpace(profile.Logout)) throw new HumanActionRequiredException("Cierre remoto pendiente.");
                    var session = SriPortal.Vault().Load(command.Account, command.TaxId!);
                    if (current?.Account == command.Account) await Close();
                    await using var remoteBrowser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
                    var remoteContext = await remoteBrowser.NewContextAsync(new() { StorageState = session.StorageState });
                    var remotePage = await remoteContext.NewPageAsync(); remotePage.SetDefaultTimeout(10000);
                    await remotePage.GotoAsync(SriPortal.PortalUrl,new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=20000});
                    var manager = new SriSessionManager(profile); await manager.Ensure(remotePage,command.TaxId!); await manager.Logout(remotePage);
                    await remotePage.Locator(profile.Login).First.WaitForAsync(new(){State=WaitForSelectorState.Visible});
                    confirmed = await InteractiveNavigation.IsOfficialDocument(remotePage);
                } catch (Exception) { /* Cierre remoto no confirmado; revocación local obligatoria. */ }
                finally { SriPortal.Vault().Revoke(command.Account); }
                using var result = await client.PostAsJsonAsync("/api/internal/agent/report",new {operation=command.Operation,status="Cancelled",browserOpened=false,remoteLogoutConfirmed=confirmed});
                result.EnsureSuccessStatusCode(); return;
            }
            if (command.Command is "Cancel" or "Logout")
            {
                if (current?.Account == command.Account) await Close();
                SriPortal.Vault().Revoke(command.Account);
                await Send(command.Operation, "Cancelled", false); return;
            }
            if(command.Command=="Run") {
                if(current?.Operation!=command.Operation||page is null||!accessConfirmed||command.ExecutionId is null){await Send(command.Operation,"QueryFailed",false);return;}
                if(running?.IsCompleted==false)return;
                current=command;runCancel=new();var target=page;running=Task.Run(()=>RunQuery(command,target,runCancel.Token));return;
            }
            if(command.Command=="Show"){
                if(current?.Operation==command.Operation){var visible=LivePage();if(visible is not null)await WindowState(visible,"normal");}
                return;
            }
            if (command.Command == "Open")
            {
                await Close(); current = command;
                try { browser = await playwright.Chromium.LaunchAsync(new() { Headless = false,Channel="msedge" }); }
                catch(PlaywrightException) { browser = await playwright.Chromium.LaunchAsync(new() { Headless = false }); }
                SriPortal.Vault().Revoke(command.Account);
                var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
                page = await context.NewPageAsync(); page.SetDefaultTimeout(5000);
                await page.BringToFrontAsync();
                if (!browser.IsConnected || page.IsClosed) throw new InvalidOperationException();
                openedAt=DateTimeOffset.UtcNow;lastObservation="";portalLoaded=false;accessConfirmed=false;accessReported=false;boundSession=null;queryPaused=false;credentialAttemptedAt=null;
                await Report("InspectingPortal", true);
                navigation = Navigate(page, generation, command.Operation); return;
            }
            if (current?.Operation != command.Operation || page is null || page.IsClosed || browser?.IsConnected != true)
            { await Send(command.Operation, "Failed", false); return; }
            if (command.Command == "Reload") { navigation = Navigate(page, generation, command.Operation); return; }
            if (command.Command == "Verify")
            {
                if(accessConfirmed)return;
                if (navigation?.IsCompleted == false) {await Send(command.Operation,"VerificationFailed",true,"LoginRequired");return;}
                await Report("InspectingPortal", true);
                await ObserveAccess(true);return;
            }
        }
        catch (Exception ex) when (ex is HumanActionRequiredException or ArgumentException or TimeoutException or PlaywrightException)
        { SriPortal.Vault().Revoke(command.Account); await Send(command.Operation, page is { IsClosed: false } ? "RequiresHumanAction" : "Failed", page is { IsClosed: false }); }
        catch (Exception)
        { SriPortal.Vault().Revoke(command.Account); await Send(command.Operation, "Failed", page is { IsClosed: false }); }
    }
    private async Task Navigate(IPage target, int version, string operation)
    {
        try
        {
            var loaded = await InteractiveNavigation.TryOpen(target, SriAccessEvidence.LoginUrl);
            if(version==generation)portalLoaded=loaded;
            if(!loaded&&version==generation) {await Close();await Send(operation,"PortalUnavailable",false);return;}
            if (version == generation && !target.IsClosed) await Send(operation, loaded ? "WaitingForLogin" : "PortalUnavailable", true);
            if(loaded&&version==generation&&current is {} op){
                await Trace(op,target,"LoginOpened");
                var loginOpened=false;
                    for(var i=0;i<60&&version==generation&&!target.IsClosed;i++){
                        if(await SriAccessEvidence.HumanChallenge(target))break;
                        if(await target.Locator("input[type=password]:visible").CountAsync()==1){if(CredentialVault.Configured().Present(op.Account)){if(await Login(op,target))credentialAttemptedAt=DateTimeOffset.UtcNow;else await Send(operation,"RequiresHumanAction",true,"AutomaticLoginUnavailable");}break;}
                        if(await SriAccessEvidence.Matches(target,op.TaxId!))break;
                        if(!loginOpened&&await InteractiveNavigation.IsOfficialDocument(target)){
                            var entry=target.GetByRole(AriaRole.Link,new(){Name="Ir a iniciar sesión",Exact=true});
                            if(await entry.CountAsync()==1&&await entry.IsVisibleAsync()){loginOpened=true;await entry.ClickAsync();}
                        }
                        await Task.Delay(500);
                    }
            }
        }
        catch (Exception ex)when(ex is PlaywrightException or TimeoutException) {if(version==generation&&!target.IsClosed)await Send(operation,"VerificationFailed",true,portalLoaded?"LoginNotConfirmed":"UnofficialOrigin");}
        catch(IOException){if(version==generation&&!target.IsClosed)await Send(operation,"RequiresHumanAction",true,"CredentialUnavailable");}
    }
    private async Task ObserveAccess(bool requested=false)
    {
        if(current is null||page is null||page.IsClosed||navigation?.IsCompleted==false||!portalLoaded)return;
        if(queryPaused)return;
        if(accessConfirmed){if(!accessReported&&running?.IsCompleted!=false){await Send(current.Operation,"Authenticated",true,"Verified");accessReported=true;}return;}
        var op=current;var target=page;
        try {
            var check=await SriAccessEvidence.Inspect(target,op.TaxId!);
            foreach(var candidate in target.Context.Pages.Where(p=>p!=target&&!p.IsClosed)) {
                var alternative=await SriAccessEvidence.Inspect(candidate,op.TaxId!);
                if(alternative.Verified){target=candidate;check=alternative;break;}
            }
            if(check.Verified) {
                var sessionStorage=await target.EvaluateAsync<string>("()=>JSON.stringify(Object.fromEntries(Object.entries(sessionStorage)))");
                SriPortal.Vault().Save(new(op.Account,op.TaxId!,DateTimeOffset.UtcNow.AddMinutes(10),await target.Context.StorageStateAsync(),sessionStorage));
                boundSession=await BoundSriSession.Verify(target,op.TaxId!);accessConfirmed=true;verifiedAt=DateTimeOffset.UtcNow;page=target;
                await Trace(op,target,"IdentityConfirmed");
                if(op.ExecutionId is null){await Close();await Send(op.Operation,"Authenticated",false,"Verified");return;}
                await Send(op.Operation,"Authenticated",true,"Verified");accessReported=true;return;
            }
            var status=await SriAccessEvidence.HumanChallenge(target)?"RequiresHumanAction":DateTimeOffset.UtcNow-openedAt>TimeSpan.FromMinutes(10)?"PendingValidation":"";
            if(status=="PendingValidation") {await Close();await Send(op.Operation,status,false);return;}
            if(requested){lastObservation=check.Code;await Send(op.Operation,check.Code=="HumanChallenge"?"RequiresHumanAction":"VerificationFailed",true,check.Code);return;}
            if(check.Code=="SsoRequestRejected"&&lastObservation!=check.Code){lastObservation=check.Code;await Send(op.Operation,"VerificationFailed",true,check.Code);return;}
            if(check.Code=="LoginRequired"&&credentialAttemptedAt is {} attempt&&DateTimeOffset.UtcNow-attempt>TimeSpan.FromSeconds(10)&&lastObservation!="LoginNotConfirmed"){lastObservation="LoginNotConfirmed";await Send(op.Operation,"VerificationFailed",true,"LoginNotConfirmed");return;}
            if(status!=""&&status!=lastObservation){lastObservation=status;await Send(op.Operation,status,true);}
        }catch(Exception ex)when(ex is PlaywrightException or TimeoutException or PortalRecognitionException){/* Document may be transitioning after login. Never resubmit. */}
    }
    public async Task CheckClosed()
    {
        if(accessConfirmed&&current is {ExecutionId:null}&&running?.IsCompleted!=false&&DateTimeOffset.UtcNow-verifiedAt>TimeSpan.FromMinutes(10)){var op=current;await Close();SriPortal.Vault().Revoke(op.Account);await Send(op.Operation,"Cancelled",false);return;}
        if(queryPaused&&current is not null&&DateTimeOffset.UtcNow-pausedAt>TimeSpan.FromMinutes(5)){
            var operation=current.Operation;var reason=pauseReason;await Close();await Send(operation,"QueryFailed",false,reason);return;
        }
        if(current is not null&&page is not null&&page.IsClosed&&browser?.IsConnected==true){var replacement=LivePage();if(replacement is not null)page=replacement;}
        if (current is not null && page is not null && (page.IsClosed || browser?.IsConnected != true)&&running?.IsCompleted!=false)
        { var op = current; await Close(); SriPortal.Vault().Revoke(op.Account); await Send(op.Operation, "Cancelled", false); }
        else await ObserveAccess();
    }
    private async Task RunQuery(AgentCommand command,IPage target,CancellationToken cancellation)
    {
        try {
            queryResponses.Clear();
            target.Context.Response+=(_,response)=>{
                if(Uri.TryCreate(response.Url,UriKind.Absolute,out var uri)&&uri.Host=="srienlinea.sri.gob.ec"&&uri.AbsolutePath.StartsWith("/comprobantes-electronicos-internet/")&&!System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,@"\d{10}")&&response.Request.ResourceType is "document" or "xhr" or "fetch"){
                    queryResponses.Enqueue(new{method=response.Request.Method,path=uri.AbsolutePath,status=response.Status});while(queryResponses.Count>30)queryResponses.TryDequeue(out _);
                }
            };
            var remote=new RemoteExecution(client,command.ExecutionId!);
            var job=remote.Read(s=>s.Executions.Single());
            using var close=cancellation.Register(()=>{_=target.Context.CloseAsync();});
            remote.Update(s=>{ExecutionProgress.Set(s,job.Id,"OpeningQuery","Esperando la redirección SRI en la sesión autorizada.");return true;});
            var bound=boundSession??await BoundSriSession.Verify(target,command.TaxId!);
            await Trace(command,target,"QueryOpening");
            target=await AuthorizedQueryNavigation.Open(target,bound,command.TaxId!,cancellation,authenticate:CredentialVault.Configured().Present(command.Account)?async p=>{page=p;remote.Update(s=>{ExecutionProgress.Set(s,job.Id,"AuthenticatingApplication","Autenticando la aplicación de comprobantes con el perfil autorizado.");return true;});return await Login(command,p);}:null,humanAction:async p=>{page=p;remote.Update(s=>{ExecutionProgress.Set(s,job.Id,"WaitingForHuman","El SRI solicita verificación humana. Complétela en su ventana; el bot seguirá al detectarla.");return true;});await WindowState(p,"normal");});page=target;
            await Trace(command,target,"QueryReady");
            remote.Update(s=>{ExecutionProgress.Set(s,job.Id,"RecognizingControls","Reconociendo filtros en la sesión SRI autorizada.");return true;});
            var profile=await ObservedPortalProfile.Discover(target,command.TaxId!,session:bound);profile.Validate();
            var queryTrace=Path.Combine(root,".secrets","query-steps.jsonl");
            File.WriteAllText(queryTrace,"");
            profile.TraceStep=step=>File.AppendAllText(queryTrace,JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,step})+Environment.NewLine);
            var formStructure=await target.Locator("input[type=radio],input[id*=Fecha],input[id*=fecha]").EvaluateAllAsync<JsonElement>("es=>es.filter(e=>e.getClientRects().length).map(e=>({id:e.id,type:e.type,readOnly:e.readOnly,checked:e.type==='radio'?e.checked:null,labels:e.labels?Array.from(e.labels).map(l=>l.innerText):[]}))");
            await File.WriteAllTextAsync(Path.Combine(root,".secrets","native-query-form.json"),JsonSerializer.Serialize(formStructure));
            await WindowState(target,"minimized");
            await new SriExecutionEngine(remote,remote).Run(target,profile,job,command.TaxId!,cancellation);
            if(current?.Operation==command.Operation)await Close(false);await Send(command.Operation,"QueryCompleted",false);
        }catch(OperationCanceledException){if(current?.Operation==command.Operation)await Close(false);await Send(command.Operation,"Cancelled",false);}
        catch(PortalRecognitionException ex){await PauseQuery(command,target,ex.Code);}
        catch(HumanActionRequiredException){await PauseQuery(command,target,"QueryControlsMissing");}
        catch(DocumentTransferException ex){await PauseQuery(command,target,ex.Code);}
        catch(IOException){await PauseQuery(command,target,"QueryTransportFailed");}
        catch(Exception ex)when(ex is TimeoutException or PlaywrightException){await PauseQuery(command,target,"QueryTransportFailed");}
        catch(Exception){if(current?.Operation==command.Operation)await Close(false);await Send(command.Operation,"QueryFailed",false,"QueryTransportFailed");}
    }
    private async Task PauseQuery(AgentCommand command,IPage target,string reason)
    {
        try{await File.WriteAllTextAsync(Path.Combine(root,".secrets","native-query-responses.json"),JsonSerializer.Serialize(queryResponses.ToArray()));}catch(IOException){}
        if(current?.Operation!=command.Operation){await Send(command.Operation,"QueryFailed",false,reason);return;}
        target=LivePage()??target;
        if(reason is "LoginRequired" or "LoginNotConfirmed" or "AutomaticLoginUnavailable" or "HumanChallenge")foreach(var candidate in target.Context.Pages.Reverse().Where(p=>!p.IsClosed)){if(await InteractiveNavigation.IsOfficialDocument(candidate)&&(await candidate.Locator("input[type=password]:visible").CountAsync()>0||await SriAccessEvidence.HumanChallenge(candidate))){target=candidate;break;}}
        await Trace(command,target,"QueryStopped");
        if(target.IsClosed){await Send(command.Operation,"QueryFailed",false,reason);return;}
        page=target;
        queryPaused=true;pauseReason=reason;pausedAt=DateTimeOffset.UtcNow;
        // Capture only technical control structure, never values, page text, HTML or URLs.
        try{
            var controls=await target.Locator("input:not([type=password]):not([type=hidden]),select,button,form,table").EvaluateAllAsync<JsonElement>("es=>es.filter(e=>e.getClientRects().length).slice(0,300).map(e=>({tag:e.tagName,id:/^[a-zA-Z_][a-zA-Z0-9_:.-]{0,119}$/.test(e.id)&&!/[0-9]{10}/.test(e.id)?e.id:null,type:e.type||null}))");
            var navigation=await target.Locator("a,button,input[type=submit],[role=button]").EvaluateAllAsync<JsonElement>("es=>es.filter(e=>e.getClientRects().length).map(e=>({tag:e.tagName,text:(e.innerText||e.getAttribute('title')||e.getAttribute('aria-label')||(e.type==='submit'?e.value:'')||'').trim().slice(0,200),path:e.tagName==='A'&&e.href?new URL(e.href,location.href).pathname:null})).filter(e=>/comprob|consulta|emitid|recibid|factur|producci|recuper|autorizad|men[uú]|inicio|siguiente|continuar/i.test(e.text)&&!/[0-9]{10}|@/.test(e.text)).slice(0,80)");
            var access=await SriAccessEvidence.Inspect(target,command.TaxId!);
            var filters=await target.Locator("select").EvaluateAllAsync<JsonElement>("es=>es.filter(e=>e.getClientRects().length&&/tipo.*comprobante|estado.*autorizacion/i.test(e.id)).map(e=>({id:e.id,options:Array.from(e.options).map(o=>({text:o.text,value:o.value}))}))");
            var icons=await target.Locator("a img,button img,[title],[aria-label]").EvaluateAllAsync<JsonElement>("es=>es.map(e=>({tag:e.tagName,id:e.id,title:e.getAttribute('title'),aria:e.getAttribute('aria-label'),alt:e.getAttribute('alt'),image:e.tagName==='IMG'?new URL(e.src,location.href).pathname:null})).filter(e=>/salir|cerrar|logout|power|sesi/i.test(JSON.stringify(e))).slice(0,20)");
            await File.WriteAllTextAsync(Path.Combine(root,".secrets","native-navigation.json"),JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,accessCode=access.Code,navigation,icons,filters,responses=queryResponses.ToArray()}));
            using var response=await client.PostAsJsonAsync("/api/internal/agent/controls",controls);
            response.EnsureSuccessStatusCode();
        }catch(Exception){/* Diagnostics must never replace the original failure. */}
        await Send(command.Operation,"QueryFailed",true,reason);
    }
    private async Task<bool> Login(AgentCommand op,IPage target){var sent=false;await CredentialVault.Configured().Use(op.Account,op.TaxId!,async secret=>{sent=await AuthorizedLogin.Submit(target,op.TaxId!,secret);});return sent;}
    private IPage? LivePage()=>page?.Context.Pages.LastOrDefault(p=>!p.IsClosed);
    private static string Location(IPage target){if(target.IsClosed)return "Closed";if(!Uri.TryCreate(target.Url,UriKind.Absolute,out var u))return "Transition";if(u.Scheme=="about")return "Transition";if(u.Scheme.Contains("error"))return "BrowserError";if(u.Scheme!="https"||u.Host!="srienlinea.sri.gob.ec")return "OtherOrigin";return u.AbsolutePath.StartsWith("/auth/")?"SriLogin":u.AbsolutePath.Contains("/contribuyente/perfil")?"SriAccount":u.AbsolutePath.Contains("/comprobantes-electronicos-internet/")?"SriQuery":"SriOther";}
    private async Task Trace(AgentCommand op,IPage target,string phase){try{using var result=await client.PostAsJsonAsync("/api/internal/agent/observations",new{operation=op.Operation,value=new BrowserObservation(DateTimeOffset.UtcNow,phase,Location(target),target.Context.Pages.Count,Math.Min(target.Frames.Count,100),target.IsClosed)});}catch(Exception){}}
    private static async Task WindowState(IPage target,string state){try{var cdp=await target.Context.NewCDPSessionAsync(target);try{var info=await cdp.SendAsync("Browser.getWindowForTarget");await cdp.SendAsync("Browser.setWindowBounds",new Dictionary<string,object>{{"windowId",info!.Value.GetProperty("windowId").GetInt32()},{"bounds",new Dictionary<string,object>{{"windowState",state}}}});}finally{await cdp.DetachAsync();}if(state=="normal")await target.BringToFrontAsync();}catch(Exception ex)when(ex is PlaywrightException or TimeoutException){}}
    private async Task Close(bool cancelRun=true)
    {
        if(cancelRun)runCancel?.Cancel();
        generation++; var previous = browser; browser = null; page = null; current = null;
        if (previous is not null) try { await previous.CloseAsync(); } catch (PlaywrightException) { }
    }
}
