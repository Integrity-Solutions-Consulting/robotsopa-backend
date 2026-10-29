using System.Security.Cryptography;
using System.Text;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
namespace SriAutomation.Tests;
public sealed class SecureWorkflowTests
{
    [Fact]
    public void AuthenticationErrorsExposePhaseWithoutBrowserSecrets()
    {
        const string secret="password=PRIVATE&state=PRIVATE";
        var navigation=AuthenticationFailure.Message("Navigation",new TimeoutException(secret));
        Assert.Contains("antes de introducir",navigation);
        Assert.Contains("SRI-NAV-TIMEOUT",navigation);
        Assert.DoesNotContain("PRIVATE",navigation);
        var form=AuthenticationFailure.Message("LoginForm",new PlaywrightException(secret));
        Assert.Contains("SRI-FORM-BROWSER",form);
        Assert.DoesNotContain("PRIVATE",form);
        Assert.Contains("SRI-VERIFY",AuthenticationFailure.Message("SessionVerification",new TimeoutException(secret)));
    }
    [Fact]
    public async Task WorkerDelegatesToLocalBrowserAndPreservesRealFailureWithoutLaunchingAnotherLogin()
    {
        var folder=Path.Combine(Path.GetTempPath(),"sri-delegation-"+Guid.NewGuid().ToString("N"));
        try {
            var repository=new JsonStateRepository(folder);
            var account=new Account("authorized-fixture","Cuenta ficticia","1790000000001");
            repository.Update(s=>{s.Accounts.Add(account);s.AgentHeartbeat=DateTimeOffset.UtcNow;return true;});
            var query=new Query(account.Id,new(2026,10,12),new(2026,10,12),["Facturas"]);
            var job=repository.Update(s=>{var e=Validation.Enqueue(s,query);e.Status="Running";return e;});
            using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var connecting=new WorkerAuthentication(repository).Connect(account,cancellation.Token,job.Id);
            var delegated=repository.Read(s=>s.Authentications.Single());
            Assert.Equal("Agent",delegated.Owner);Assert.Equal("Open",delegated.Command);
            Assert.False(delegated.SessionVerified);
            Assert.Equal(job.Id,delegated.ExecutionId);
            Assert.Equal("OpeningBrowser",repository.Read(s=>s.Executions.Single().Stage));
            Assert.False(connecting.IsCompleted);
            repository.Update(s=>{s.Authentications.Single().BrowserOpened=true;AuthenticationFlow.ReadyExecution(s,job.Id);return true;});
            Assert.Equal("Verify",repository.Read(s=>s.Authentications.Single().Command));
            Assert.False(connecting.IsCompleted);
            Assert.False(repository.Read(s=>s.Authentications.Single().SessionVerified));
            Assert.Equal(query with{Xml=false},repository.Read(s=>s.Executions.Single().Query) with{Types=query.Types});
            repository.Update(s=>{s.Executions.Single().CancelRequested=true;return true;});
            Assert.Throws<ArgumentException>(()=>repository.Update(s=>{AuthenticationFlow.ReadyExecution(s,job.Id);return true;}));
            repository.Update(s=>{s.Authentications.Single().Status="PortalUnavailable";return true;});
            await Assert.ThrowsAsync<HumanActionRequiredException>(()=>connecting);
            Assert.Single(repository.Read(s=>s.Authentications));
            Assert.False(repository.Read(s=>s.Authentications.Single().SessionVerified));
        }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Fact]
    public void MultipleTypesAndRangesPersistAndRejectDuplicates()
    {
        var state=new State {Accounts=[new("a","QA")]};
        var query=new Query("a",new(2026,10,1),new(2026,10,12),Validation.Types,BroadRangeApproved:true);
        Assert.Equal(6,Validation.Enqueue(state,query).Query.Types.Length);
        Assert.Throws<ArgumentException>(()=>Validation.Query(query with{Types=["Facturas","Facturas"]},state));
        Assert.Throws<ArgumentException>(()=>Validation.Query(query with{From=new(2025,10,12)},state));
    }
    [Fact]
    public void DownloadSubfolderDoesNotEscapeRootOrLoseExistingPaths()
    {
        var root=Path.Combine(Path.GetTempPath(),"sri-destination-"+Guid.NewGuid().ToString("N"));
        try{
            var doc=new XmlDocumentParser().ParseXml("a",PhaseTwoTests.Xml());var original=new DocumentStorage(root);var path=original.Save(doc,"xml",PhaseTwoTests.Xml());
            var configured=new DocumentStorage(root,()=>"Nuevas");var next=configured.Save(doc,"xml",PhaseTwoTests.Xml());
            Assert.Contains(Path.Combine("09-Septiembre", "Nuevas", "a"),next);Assert.True(File.Exists(configured.Resolve(path)));Assert.True(File.Exists(configured.Resolve(next)));
            Assert.Throws<ArgumentException>(()=>new DocumentStorage(root,()=>"../escape").AccountDirectory("a"));
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [BrowserFact]
    public async Task AccessEvidenceRequiresOfficialOriginMatchingIdentityAndAuthenticatedControls()
    {
        using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();
        const string ruc="1790000000001";
        await page.SetContentAsync($"<p>Cuenta {ruc}</p><button>Cerrar sesión</button>");
        Assert.False(await SriAccessEvidence.Matches(page,ruc)); // Unofficial origin is never trusted.
        Assert.True(await SriAccessEvidence.Matches(page,ruc,true));
        Assert.False(await SriAccessEvidence.Matches(page,"1790000000002",true));
        Assert.False(await SriAccessEvidence.Matches(page,"",true));
        await page.SetContentAsync($"<p>{ruc}</p><input type='password'><button>Cerrar sesión</button>");
        Assert.False(await SriAccessEvidence.Matches(page,ruc,true));
        await page.SetContentAsync($"<p>{ruc}</p><button style='display:none'>Cerrar sesión</button>");
        Assert.False(await SriAccessEvidence.Matches(page,ruc,true));
        await page.SetContentAsync($"<p>{ruc}</p><input autocomplete='one-time-code'>");
        Assert.True(await SriAccessEvidence.HumanChallenge(page));
    }
}
