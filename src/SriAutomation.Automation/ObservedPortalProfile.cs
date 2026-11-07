using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SriAutomation.Application;
namespace SriAutomation.Automation;

// Discovers the controls of the document actually loaded. Never supplies assumed SRI IDs.
public static class ObservedPortalProfile
{
    private static string Normal(string text)=>Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormD),@"\p{Mn}|[^a-zA-Z0-9]","").ToLowerInvariant();
    private static string? TypeName(string text){var name=Normal(text);return Regex.IsMatch(name,"notas?decredito")?"Notas-Credito":Regex.IsMatch(name,"notas?dedebito")?"Notas-Debito":name.Contains("retencion")?"Retenciones":name.Contains("liquidacion")?"Liquidaciones":name.Contains("guia")&&name.Contains("remision")?"Guias-Remision":Regex.IsMatch(name,"^facturas?[0-9]*$")?"Facturas":null;}
    public static async Task<bool> HasTypeControl(IPage page){foreach(var select in await page.Locator("select:visible").AllAsync())foreach(var text in await select.Locator("option").AllTextContentsAsync())if(TypeName(text) is not null)return true;return false;}
    private static async Task<string> Selector(ILocator control)
    {
        var id=await control.GetAttributeAsync("id");
        if(!string.IsNullOrWhiteSpace(id))return "[id="+JsonSerializer.Serialize(id)+"]";
        var marker=Guid.NewGuid().ToString("N");await control.EvaluateAsync("(e,id)=>e.setAttribute('data-sri-observed',id)",marker);
        return "[data-sri-observed="+JsonSerializer.Serialize(marker)+"]";
    }
    private static Task<string> Label(ILocator control)=>control.EvaluateAsync<string>("e=>[e.id,e.name,e.getAttribute('aria-label'),e.getAttribute('placeholder'),...(e.labels?Array.from(e.labels).map(l=>l.innerText):[])].join(' ')");
    public static async Task<PortalProfile> Discover(IPage page,string taxId,bool fixture=false,BoundSriSession? session=null)
    {
        if(!fixture){var check=session is null?await SriAccessEvidence.Inspect(page,taxId):await session.Inspect(page,taxId);if(!check.Verified)throw new PortalRecognitionException(check.Code=="IdentityMismatch"?"QueryAccountMissing":check.Code);}
        var issued=page.GetByRole(AriaRole.Link,new(){NameRegex=new Regex("^(consulta de )?(comprobantes (electr[oó]nicos )?)?emitidos$",RegexOptions.IgnoreCase)})
            .Or(page.GetByRole(AriaRole.Button,new(){NameRegex=new Regex("^(consulta de )?(comprobantes (electr[oó]nicos )?)?emitidos$",RegexOptions.IgnoreCase)}));
        if(!await HasTypeControl(page)){var visible=new List<ILocator>();foreach(var c in await issued.AllAsync())if(await c.IsVisibleAsync())visible.Add(c);if(visible.Count==1)await visible[0].ClickAsync();}
        try{await page.Locator("select:visible").First.WaitForAsync(new(){Timeout=20000});}
        catch(TimeoutException){throw new PortalRecognitionException("QuerySectionMissing");}
        var profile=new PortalProfile{Url=new Uri(page.Url).GetLeftPart(UriPartial.Path),RuntimeObserved=true,Verified=true,VerifiedAt=DateTimeOffset.UtcNow.ToString("O"),Evidence="Controles observados en el documento actual; resultados y archivos se verifican durante la operación.",AccountIdentity="body",Authenticated="body",Login="input[type=password]:visible",HumanChallenge="input[autocomplete=one-time-code]:visible",Ready="body",ResponseUrlPattern=Regex.Escape(new Uri(page.Url).GetLeftPart(UriPartial.Path)),MaxAutomaticDays=31,MaxDocuments=10};
        var ajax=new List<string>();
        profile.BoundSession=session;
        foreach(var select in await page.Locator("select:visible").AllAsync()) {
            var label=Normal(await Label(select));
            var options=(await select.Locator("option").EvaluateAllAsync<JsonElement>("es=>es.map(e=>({value:e.value,text:e.textContent||''}))")).EnumerateArray().Select(o=>new Option(o.GetProperty("value").GetString()!,o.GetProperty("text").GetString()!)).ToArray();
            var mapped=new Dictionary<string,string>();
            var ambiguous=new HashSet<string>();
            foreach(var option in options) {
                var type=TypeName(option.Text);
                if(type is not null){if(mapped.ContainsKey(type))ambiguous.Add(type);else mapped[type]=option.Value;}
            }
            // Several retention versions must not block a uniquely identified Factura.
            // Ambiguous types stay unavailable and cannot be selected by Search.
            foreach(var type in ambiguous)mapped.Remove(type);
            string? kind=null;
            if(mapped.Count>0){if(profile.Type!="")throw new HumanActionRequiredException("Hay varios filtros de comprobantes; no se pudo elegir uno de forma segura.");profile.Type=await Selector(select);profile.TypeValues=mapped;kind="type";}
            else if(label.Contains("anio")||label.Contains("ano")||label.Contains("year")){profile.Year=await Selector(select);kind="year";}
            else if(label.Contains("mes")||label.Contains("month")){profile.Month=await Selector(select);kind="month";}
            else if(label.Contains("dia")||label.Contains("day")){profile.Day=await Selector(select);kind="day";}
            else if(label.Contains("estado")) {profile.Status=await Selector(select);foreach(var option in options)if(Normal(option.Text) is "autorizado" or "autorizados")profile.StatusValues["Autorizado"]=option.Value;}
            if(kind is not null&&Regex.IsMatch(await select.GetAttributeAsync("onchange")??"","ajax",RegexOptions.IgnoreCase))ajax.Add(kind);
        }
        foreach(var input in await page.Locator("input:not([type=hidden]):not([type=password]):visible").AllAsync()) {
            var label=Normal(await Label(input));
            if(label.Contains("fecha")&&!label.Contains("autorizacion")){if(profile.Date!="")throw new HumanActionRequiredException("El portal muestra varias fechas; no se pudo reconocer la fecha de emisión.");profile.Date=await Selector(input);profile.DateFormat=await input.GetAttributeAsync("type")=="date"?"yyyy-MM-dd":"dd/MM/yyyy";}
            else if(label.Contains("establecimiento"))profile.Establishment=await Selector(input);
            else if(label.Contains("punto")&&label.Contains("emision"))profile.EmissionPoint=await Selector(input);
        }
        var search=page.GetByRole(AriaRole.Button,new(){NameRegex=new Regex("^(consultar|buscar)$",RegexOptions.IgnoreCase)}).Or(page.GetByRole(AriaRole.Link,new(){NameRegex=new Regex("^(consultar|buscar)$",RegexOptions.IgnoreCase)}));
        var candidates=new List<ILocator>();foreach(var c in await search.AllAsync())if(await c.IsVisibleAsync())candidates.Add(c);
        if(profile.Type=="")throw new PortalRecognitionException("QueryTypeMissing");
        if(candidates.Count!=1)throw new PortalRecognitionException("QuerySearchMissing");
        if(profile.Date==""&&(profile.Year is null||profile.Month is null||profile.Day is null))throw new PortalRecognitionException("QueryDateMissing");
        profile.Search=await Selector(candidates[0]);
        var formAction=await candidates[0].EvaluateAsync<string?>("e=>e.form?.action||e.closest('form')?.action||null");
        if(formAction is not null){
            if(!Uri.TryCreate(formAction,UriKind.Absolute,out var action)||!fixture&&(action.Scheme!="https"||action.Host!="srienlinea.sri.gob.ec"||!action.IsDefaultPort||!action.AbsolutePath.StartsWith("/comprobantes-electronicos-internet/pages/consultas/",StringComparison.Ordinal)))throw new PortalRecognitionException("QuerySearchMissing");
            // JSF may display the form under menu.jsf while posting to recuperarComprobantes.jsf.
            // Observe the actual owning form, excluding its query/tokens from the profile.
            profile.ResponseUrlPattern="^"+Regex.Escape(action.GetLeftPart(UriPartial.Path))+@"(?:[?;]|$)";
        }
        profile.AjaxFilters=ajax.ToArray();return profile;
    }
    public static async Task Results(IPage page,PortalProfile profile)
    {
        var empty=page.GetByText(new Regex(@"^(No (se )?(encontraron|existen|hay).*(comprobantes|registros|resultados|documentos|datos)|Sin (comprobantes|registros|resultados|documentos|datos)|No records found).*$",RegexOptions.IgnoreCase));
        var tableCandidate=page.Locator("table:visible").Filter(new(){Has=page.GetByText(new Regex("clave.*acceso|n[uú]mero.*autorizaci[oó]n",RegexOptions.IgnoreCase))});
        await tableCandidate.Or(empty.And(page.Locator(":visible"))).First.WaitForAsync(new(){State=WaitForSelectorState.Visible,Timeout=20000});
        foreach(var e in await empty.AllAsync())if(await e.IsVisibleAsync()){profile.Empty=await Selector(e);profile.Rows="[data-sri-empty-result-row]";profile.Next=profile.NextDisabled="[data-sri-no-next]";return;}
        var tables=new List<(ILocator Table,int Key,int Date)>();
        foreach(var table in await page.Locator("table:visible").AllAsync()) {
            var headers=(await table.Locator("thead th").AllTextContentsAsync()).Select(Normal).ToArray();
            var key=Array.FindIndex(headers,h=>h.Contains("clave")&&h.Contains("acceso")||h.Contains("numero")&&h.Contains("autorizacion"));
            var date=Array.FindIndex(headers,h=>h.Contains("fecha")&&h.Contains("emision"));
            if(key>=0&&date>=0)tables.Add((table,key,date));
        }
        if(tables.Count!=1)throw new HumanActionRequiredException("El SRI respondió, pero no se pudo identificar con seguridad la tabla de comprobantes y su fecha de emisión.");
        var found=tables[0];var selector=await Selector(found.Table);profile.Rows=selector+" tbody > tr";profile.Key=$"td:nth-child({found.Key+1})";profile.Issued=$"td:nth-child({found.Date+1})";profile.Empty="[data-sri-no-empty-message]";
        profile.Next=profile.NextDisabled="[data-sri-no-next]";
        foreach(var c in await page.Locator("button,a,[role=button]").AllAsync()) {
            if(!await c.IsVisibleAsync())continue;
            var description=await c.EvaluateAsync<string>("e=>[e.innerText,e.getAttribute('title'),e.getAttribute('aria-label'),e.className,...Array.from(e.querySelectorAll('span,i')).map(x=>x.className)].join(' ')");
            if(!Regex.IsMatch(description,@"\b(next|siguiente|seek-next)\b",RegexOptions.IgnoreCase))continue;
            if(profile.Next!="[data-sri-no-next]")throw new HumanActionRequiredException("No se pudo reconocer una única acción de página siguiente.");
            profile.Next=await Selector(c);profile.NextDisabled=profile.Next+"[disabled],"+profile.Next+"[aria-disabled=true],"+profile.Next+".ui-state-disabled";
        }
    }
    public static async Task<ILocator?> DownloadControl(ILocator row,string extension)
    {
        var matches=new List<ILocator>();foreach(var c in await row.Locator("a,button,input[type=submit],input[type=image]").AllAsync()) {
            if(!await c.IsVisibleAsync())continue;
            var description=await c.EvaluateAsync<string>("e=>[e.innerText,e.getAttribute('title'),e.getAttribute('aria-label'),e.getAttribute('alt'),e.getAttribute('src'),e.getAttribute('href'),...Array.from(e.querySelectorAll('img')).flatMap(x=>[x.alt,x.getAttribute('src')])].join(' ')");
            if(Regex.IsMatch(description,extension=="pdf"?@"\b(pdf|ride)\b":@"\bxml\b",RegexOptions.IgnoreCase))matches.Add(c);
        }
        if(matches.Count>1)throw new InvalidDataException("Hay varias descargas del mismo formato en la fila.");return matches.SingleOrDefault();
    }
    private sealed record Option(string Value,string Text);
}
