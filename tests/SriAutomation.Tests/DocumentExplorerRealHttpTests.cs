using System.Text.Json;
using Microsoft.Playwright;

namespace SriAutomation.Tests;

public class DocumentExplorerRealHttpTests
{
    [BrowserFact]
    public async Task ChromiumUsesRealApiForExplorerAndZip()
    {
        var fixturePath = Environment.GetEnvironmentVariable("SOPA_HTTP_FIXTURE") ?? throw new InvalidOperationException("Falta SOPA_HTTP_FIXTURE");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var user = fixture.RootElement.GetProperty("clients")[0];
        var account = user.GetProperty("id").GetString()!;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        foreach (var theme in new[] { "light", "dark" })
        {
            await using var context = await browser.NewContextAsync();
            await context.AddInitScriptAsync($"localStorage.setItem('sri-theme','{theme}')");
            var page = await context.NewPageAsync();
            var downloads = 0; page.Download += (_, _) => downloads++;
            await page.GotoAsync("http://127.0.0.1:59042/comprobantes");
            await page.GetByLabel("Usuario", new() { Exact = true }).FillAsync(user.GetProperty("user").GetString()!);
            await page.GetByLabel("Contraseña del panel").FillAsync(fixture.RootElement.GetProperty("password").GetString()!);
            await page.GetByRole(AriaRole.Button, new() { Name = "Entrar al panel" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Mis documentos", Exact = true }).ClickAsync();
            var explorer = page.Locator("sri-explorador");
            await explorer.GetByLabel("RUC", new() { Exact = true }).SelectOptionAsync(account);
            await explorer.GetByRole(AriaRole.Button, new() { Name = "2026", Exact = true }).ClickAsync();
            await explorer.GetByRole(AriaRole.Button, new() { Name = "10 Octubre", Exact = true }).ClickAsync();
            await explorer.GetByRole(AriaRole.Button, new() { Name = "Facturas (1)", Exact = true }).ClickAsync();
            await explorer.GetByPlaceholder("Emisor, receptor, número o clave").FillAsync("FICTICIO");
            await explorer.GetByLabel("Ordenar").SelectOptionAsync("total");
            await explorer.GetByRole(AriaRole.Button, new() { Name = "Aplicar", Exact = true }).ClickAsync();
            await explorer.GetByRole(AriaRole.Button, new() { Name = "Ver detalle", Exact = true }).ClickAsync();
            await Assertions.Expect(explorer.GetByText("EMISOR FICTICIO", new() { Exact = false }).Last).ToBeVisibleAsync();
            Assert.Equal(0, downloads);
            await Assertions.Expect(explorer.GetByRole(AriaRole.Link, new() { Name = "Descargar PDF" })).ToBeVisibleAsync();
            await Assertions.Expect(explorer.GetByRole(AriaRole.Link, new() { Name = "Descargar XML" })).ToBeVisibleAsync();
            foreach (var extension in new[] { "PDF", "XML" })
            {
                var file = await page.RunAndWaitForDownloadAsync(() => explorer.GetByRole(AriaRole.Link, new() { Name = "Descargar " + extension }).ClickAsync());
                Assert.EndsWith("." + extension.ToLowerInvariant(), file.SuggestedFilename);
                Assert.True(new FileInfo((await file.PathAsync())!).Length > 0);
            }
            await explorer.GetByLabel("Formatos del ZIP").SelectOptionAsync("xml");
            await explorer.GetByRole(AriaRole.Button, new() { Name = "Preparar ZIP de esta carpeta" }).ClickAsync();
            var link = explorer.GetByRole(AriaRole.Link, new() { Name = "Descargar ZIP preparado" });
            await Assertions.Expect(link).ToBeVisibleAsync(new() { Timeout = 30000 });
            var download = await page.RunAndWaitForDownloadAsync(() => link.ClickAsync());
            Assert.EndsWith(".zip", download.SuggestedFilename);
            Assert.True(new FileInfo((await download.PathAsync())!).Length > 0);
            Assert.Equal(theme, await page.Locator("html").GetAttributeAsync("data-theme"));
            await explorer.GetByRole(AriaRole.Button, new() { Name = "10 Octubre", Exact = true }).ClickAsync();
            await explorer.GetByRole(AriaRole.Button, new() { Name = "Reportes", Exact = true }).ClickAsync();
            var excel = await page.RunAndWaitForDownloadAsync(() => explorer.GetByRole(AriaRole.Link, new() { Name = "Descargar Excel" }).ClickAsync());
            Assert.EndsWith(".xlsx", excel.SuggestedFilename);
            Assert.True(new FileInfo((await excel.PathAsync())!).Length > 0);
        }
    }
}
