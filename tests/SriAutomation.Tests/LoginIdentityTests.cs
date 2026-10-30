using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class LoginIdentityTests
{
    [Fact]
    public async Task LoginUsesEachUsersOwnCredentialsAndIdentity()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SriAutomation.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var apiDll = Path.Combine(root.FullName, "src", "SriAutomation.Api", "bin", "Debug", "net10.0", "SriAutomation.Api.dll");
        Assert.True(File.Exists(apiDll), "Compile la API antes de ejecutar esta prueba.");
        var directory = Path.Combine(Path.GetTempPath(), "sri-login-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            var db = new PanelDatabase(directory);
            db.Initialize("Cristopher", "cristopher-test-password");
            db.SaveUser("admin", "admin", "admin-test-password");
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            start.ArgumentList.Add(apiDll);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            start.Environment["ConnectionStrings__DefaultConnection"] = "";
            start.Environment["SRI_STATE_PATH"] = directory;
            start.Environment["SRI_OUTPUT_PATH"] = directory;
            start.Environment["SRI_ADMIN_USERNAME"] = "admin";
            start.Environment["SRI_ADMIN_PASSWORD"] = "cristopher-test-password";
            process = Process.Start(start)!;
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var ready = false;
            for (var i = 0; i < 100 && !process.HasExited; i++)
            {
                try { ready = (await client.GetAsync("/health")).IsSuccessStatusCode; if (ready) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.True(ready, "La API de prueba no arrancó.");

            async Task Check(string username, string password, string expectedUser, bool mustChange)
            {
                // Keep audit writes on the first-create path in restricted Windows environments.
                File.Delete(Path.Combine(directory, "state.json"));
                var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(expectedUser, body.GetProperty("user").GetString());
                Assert.Equal("admin", body.GetProperty("role").GetString());
                Assert.Equal(mustChange, body.GetProperty("mustChange").GetBoolean());
            }
            await Check("ADMIN", "admin-test-password", "admin", true);
            await Check("cRiStOpHeR", "cristopher-test-password", "Cristopher", false);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "admin", password = "cristopher-test-password" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "Cristopher", password = "admin-test-password" })).StatusCode);
        }
        finally
        {
            if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); process.Dispose(); }
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
