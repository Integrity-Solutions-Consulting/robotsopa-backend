using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Automation;

public sealed record PortalRow(string Key, DateOnly Issued, ILocator Locator);
public enum DownloadControlState { Available, Unavailable, Unrecognized }
public sealed record DownloadResult(byte[]? Bytes, DownloadControlState State);
public sealed class SriQueryPage(IPage page, PortalProfile profile)
{
    public async Task Navigate(CancellationToken cancellation)
    {
        foreach (var selector in profile.Navigation) { cancellation.ThrowIfCancellationRequested(); await page.Locator(selector).ClickAsync(); }
        await page.Locator(string.IsNullOrEmpty(profile.Date) ? profile.Year! : profile.Date).WaitForAsync(new() { State = WaitForSelectorState.Visible });
    }
    public async Task Search(Query query, DateOnly date, string type, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        profile.TraceStep?.Invoke("SelectingDate");
        if (!profile.TypeValues.TryGetValue(type, out var value)) throw new HumanActionRequiredException("Falta un valor de tipo de comprobante verificado en el perfil.");
        if (!string.IsNullOrWhiteSpace(profile.Date))
        {
            await Change("date", async () => { await page.Locator(profile.Date).FillAsync(date.ToString(profile.DateFormat, CultureInfo.InvariantCulture)); await page.Locator(profile.Date).PressAsync("Tab"); });
        }
        else
        {
            await Change("year", async () => { await page.Locator(profile.Year!).SelectOptionAsync(date.Year.ToString(CultureInfo.InvariantCulture)); });
            await Change("month", async () => { await page.Locator(profile.Month!).SelectOptionAsync(date.ToString(profile.MonthFormat, CultureInfo.InvariantCulture)); });
            await Change("day", async () => { await page.Locator(profile.Day!).SelectOptionAsync(date.ToString(profile.DayFormat, CultureInfo.InvariantCulture)); });
        }
        profile.TraceStep?.Invoke("SelectingType");
        await Change("type", async () => { await page.Locator(profile.Type).SelectOptionAsync(value); });
        await OptionalInput(profile.Establishment, query.Establishment);
        await OptionalInput(profile.EmissionPoint, query.EmissionPoint);
        if (!string.IsNullOrWhiteSpace(profile.Status))
        {
            profile.TraceStep?.Invoke("SelectingAuthorizationStatus");
            if (!profile.StatusValues.TryGetValue(query.AuthorizationStatus, out var status)) throw new HumanActionRequiredException("Estado de autorización sin valor verificado.");
            await page.Locator(profile.Status).SelectOptionAsync(status);
        }
        profile.TraceStep?.Invoke("SubmittingQuery");
        await Update(() => page.Locator(profile.Search).ClickAsync(), cancellation);
        profile.TraceStep?.Invoke("QueryResponseConfirmed");
    }
    private async Task Change(string name, Func<Task> action)
    {
        if (!profile.AjaxFilters.Contains(name)) { await action(); return; }
        var pattern = new Regex(profile.ResponseUrlPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var response = await page.RunAndWaitForResponseAsync(action, r => pattern.IsMatch(r.Url), new() { Timeout = profile.TimeoutMs });
        if (response.Status >= 400) throw new IOException("No se confirmó la actualización del filtro JSF.");
        await response.FinishedAsync();
        if (!string.IsNullOrEmpty(profile.Busy)) await page.Locator(profile.Busy).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
    }
    private async Task OptionalInput(string? selector, string? value)
    {
        if (string.IsNullOrEmpty(selector)) { if (!string.IsNullOrEmpty(value)) throw new HumanActionRequiredException("Filtro solicitado sin selector verificado."); return; }
        await page.Locator(selector).FillAsync(value ?? "");
    }
    private async Task Update(Func<Task> action, CancellationToken cancellation)
    {
        var pattern = new Regex(profile.ResponseUrlPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var response = await page.RunAndWaitForResponseAsync(action, response => pattern.IsMatch(response.Url), new() { Timeout = profile.TimeoutMs });
        if (response.Status >= 400) throw new SriUnavailableException("El SRI no respondió correctamente a la consulta.");
        await response.FinishedAsync();
        if (page.Url.Contains("/auth/", StringComparison.OrdinalIgnoreCase) || await page.Locator(profile.Login).First.IsVisibleAsync()) throw new HumanActionRequiredException("La sesión SRI expiró durante la consulta. Autentique nuevamente y reanude.");
        if (!string.IsNullOrWhiteSpace(profile.HumanChallenge) && await page.Locator(profile.HumanChallenge).First.IsVisibleAsync()) throw new HumanActionRequiredException("El portal solicita verificación humana.");
        if (!string.IsNullOrWhiteSpace(profile.Busy)) await page.Locator(profile.Busy).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await page.Locator(profile.Ready).WaitForAsync(new() { State = WaitForSelectorState.Visible });
        if(profile.RuntimeObserved)await ObservedPortalProfile.Results(page,profile);
        await page.Locator(profile.Rows).First.Or(page.Locator(profile.Empty)).First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        cancellation.ThrowIfCancellationRequested();
    }
    public async Task<List<PortalRow>> Rows()
    {
        if (await page.Locator(profile.Empty).IsVisibleAsync()) return [];
        var rows = page.Locator(profile.Rows); var result = new List<PortalRow>();
        for (var i = 0; i < await rows.CountAsync(); i++)
        {
            var row = rows.Nth(i);
            var key = new string((await row.Locator(profile.Key).InnerTextAsync()).Where(char.IsAsciiDigit).ToArray());
            if (key.Length != 49) throw new InvalidDataException("La fila no permite identificar una clave de acceso fiable.");
            var rawDate = (await row.Locator(profile.Issued).InnerTextAsync()).Trim();
            if (!DateOnly.TryParseExact(rawDate, profile.IssuedFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued)) throw new InvalidDataException("Fecha de emisión de la fila no interpretable.");
            result.Add(new(key, issued, row));
        }
        return result;
    }
    public async Task<bool> Next(CancellationToken cancellation)
    {
        var next = page.Locator(profile.Next);
        if (await next.CountAsync() == 0 || !await next.IsVisibleAsync() || await next.IsDisabledAsync() || await page.Locator(profile.NextDisabled).IsVisibleAsync()) return false;
        var before = string.Join("|", (await Rows()).Select(r => r.Key));
        await Update(() => next.ClickAsync(), cancellation);
        var after = string.Join("|", (await Rows()).Select(r => r.Key));
        if (before == after) throw new InvalidDataException("La paginación no avanzó; se detuvo para evitar un bucle.");
        return true;
    }
    public async Task TraceRowStructure(PortalRow row)
    {
        if (profile.TraceStep is null) return;
        try {
            // Structural categories only; never row text, attributes, links or scripts.
            var structure=await row.Locator.EvaluateAsync<string>("e=>JSON.stringify(Array.from(e.cells).slice(-8).map((c,i,a)=>({fromRight:a.length-i,header:(()=>{let h=e.closest('table')?.querySelectorAll('thead tr:last-child th');let t=h?.length===e.cells.length?h[c.cellIndex]?.textContent?.trim().toLowerCase():'';return /^(xml|pdf|ride)$/.test(t||'')?t:'other'})(),controls:Array.from(c.querySelectorAll('a,button,input,[role=button]')).slice(0,20).map(x=>({tag:['A','BUTTON','INPUT'].includes(x.tagName)?x.tagName:'OTHER',type:x.tagName==='INPUT'&&['submit','image','button'].includes(x.type)?x.type:null,visible:!!(x.offsetWidth||x.offsetHeight||x.getClientRects().length),disabled:x.matches(':disabled,[aria-disabled=true]'),href:x.hasAttribute('href'),onclick:x.hasAttribute('onclick'),target:x.hasAttribute('target'),download:x.hasAttribute('download'),icons:Array.from(x.querySelectorAll('img,span,i,svg')).map(y=>y.tagName).slice(0,10)}))})))");
            profile.TraceStep.Invoke($"DownloadRowStructure {structure}");
        } catch (PlaywrightException) { profile.TraceStep.Invoke("DownloadRowStructureError PlaywrightException"); }
    }
    public async Task<byte[]?> Download(PortalRow row, string extension, CancellationToken cancellation)
        => (await DownloadWithStatus(row, extension, cancellation)).Bytes;
    public async Task<DownloadResult> DownloadWithStatus(PortalRow row, string extension, CancellationToken cancellation)
    {
        if(profile.RuntimeObserved) {
            profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlChecking");
            ILocator? observed;
            try { observed=await ObservedPortalProfile.DownloadControl(row.Locator,extension); }
            catch (Exception ex) when (ex is InvalidDataException or PlaywrightException) { profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlError {ex.GetType().Name}"); throw; }
            if(observed is null){
                var missing = await MissingControlState(row, extension);
                profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlMissing state={missing}");return new(null, missing);
            }
            profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlFound");
            return new(await Transfer(observed), DownloadControlState.Available);
        }
        var selector = extension == "xml" ? profile.Xml : profile.Pdf;
        if (string.IsNullOrWhiteSpace(selector)) return new(null, DownloadControlState.Unrecognized);
        var control = row.Locator.Locator(selector);
        if (await control.CountAsync() == 0 || !await control.First.IsVisibleAsync() || await control.First.IsDisabledAsync()) return new(null, await MissingControlState(row, extension));
        if (await control.CountAsync() != 1) throw new InvalidDataException("Hay varias descargas del mismo formato en la fila.");
        profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlFound");
        return new(await Transfer(control), DownloadControlState.Available);
        async Task<byte[]?> Transfer(ILocator control) {
        for (var attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ClickAttempted");
                var download = await page.RunAndWaitForDownloadAsync(async () => { await control.ClickAsync(); profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ClickCompleted"); }, new() { Timeout = profile.TimeoutMs });
                profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}Received");
                if (await download.FailureAsync() is not null) throw new IOException("Descarga incompleta.");
                await using var source = await download.CreateReadStreamAsync() ?? throw new IOException("Descarga sin contenido.");
                using var bytes = new MemoryStream(); var buffer = new byte[81920];
                while (true) { var count = await source.ReadAsync(buffer, cancellation); if (count == 0) break; if (bytes.Length + count > 20_000_000) throw new InvalidDataException("Descarga demasiado grande."); bytes.Write(buffer, 0, count); }
                if (bytes.Length == 0) throw new InvalidDataException("Descarga vacía.");
                profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}Completed");
                return bytes.ToArray();
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) {
                profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}{(ex is TimeoutException ? "Timeout" : "Failed")}");
                if (attempt >= 2) throw;
            }
        }
        }
    }
    private static async Task<DownloadControlState> MissingControlState(PortalRow row, string extension)
    {
        // Only an explicitly identified, empty format column confirms absence for this observation.
        var cells = await row.Locator.Locator(":scope > td").AllAsync();
        var headers = await row.Locator.Locator("xpath=ancestor::table[1]").Locator("thead tr:last-child > th").AllTextContentsAsync();
        if (headers.Count != cells.Count) return DownloadControlState.Unrecognized;
        var indexes = Enumerable.Range(0, headers.Count).Where(i => string.Equals(headers[i].Trim(), extension, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (indexes.Length != 1) return DownloadControlState.Unrecognized;
        var cell = cells[indexes[0]];
        if (await cell.Locator("a,button,input,[role=button],img,svg").CountAsync() != 0 || !string.IsNullOrWhiteSpace(await cell.InnerTextAsync())) return DownloadControlState.Unrecognized;
        return DownloadControlState.Unavailable;
    }
}
