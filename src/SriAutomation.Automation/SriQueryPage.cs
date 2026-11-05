using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Automation;

public sealed record PortalRow(string Key, DateOnly Issued, ILocator Locator);
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
    public async Task<byte[]?> Download(PortalRow row, string extension, CancellationToken cancellation)
    {
        if(profile.RuntimeObserved) {
            var observed=await ObservedPortalProfile.DownloadControl(row.Locator,extension);
            if(observed is null){
                // Record only generic technical attributes; never row text, links or values.
                var structure=await row.Locator.Locator("a,button,input,span,i,img").EvaluateAllAsync<string>("es=>JSON.stringify(es.slice(0,80).map(e=>({tag:e.tagName,type:e.tagName==='INPUT'?e.type:null,classes:(typeof e.className==='string'?e.className:'').split(/\\s+/).filter(c=>/^[a-z_-]{1,60}$/i.test(c)),image:e.tagName==='IMG'&&/^[a-z_-]+\\.[a-z]+$/i.test((e.getAttribute('src')||'').split('/').pop())?(e.getAttribute('src')||'').split('/').pop():null,hints:[e.getAttribute('title'),e.getAttribute('aria-label'),e.getAttribute('alt'),e.textContent].map(v=>/xml/i.test(v||'')?'xml':/pdf|ride/i.test(v||'')?'pdf':null).filter(Boolean)})))");
                profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlMissing {structure}");return null;
            }
            profile.TraceStep?.Invoke($"Download{extension.ToUpperInvariant()}ControlFound");
            return await Transfer(observed);
        }
        var selector = extension == "xml" ? profile.Xml : profile.Pdf;
        if (string.IsNullOrWhiteSpace(selector)) return null;
        var control = row.Locator.Locator(selector);
        if (await control.CountAsync() == 0 || !await control.IsVisibleAsync() || await control.IsDisabledAsync()) return null;
        return await Transfer(control);
        async Task<byte[]?> Transfer(ILocator control) {
        for (var attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var download = await page.RunAndWaitForDownloadAsync(() => control.ClickAsync(), new() { Timeout = profile.TimeoutMs });
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
}
