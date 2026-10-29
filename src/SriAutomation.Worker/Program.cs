using SriAutomation.Worker;
using SriAutomation.Infrastructure;
using SriAutomation.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

if (args.Contains("--health"))
{
    if (string.Equals(Environment.GetEnvironmentVariable("SRI_WORKER_PAUSED"), "true", StringComparison.OrdinalIgnoreCase)) return;
    var healthRepository = new JsonStateRepository(Environment.GetEnvironmentVariable("SRI_STATE_PATH") ?? Path.GetFullPath("storage"));
    Environment.ExitCode = healthRepository.Read(s => s.WorkerHeartbeat > DateTimeOffset.UtcNow.AddSeconds(-30)) ? 0 : 1;
    return;
}
if (args.Contains("--probe")) { await PortalProbe.Run("docs/sri-public-controls.json"); return; }
if (args.Contains("--revoke-session")) { var i = Array.IndexOf(args, "--revoke-session"); SriPortal.Vault().Revoke(args[i + 1]); return; }
if (args.Contains("--logout"))
{
    string Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Falta argumento: " + key); }
    await SriPortal.Logout(Arg("--account"), Arg("--tax-id")); return;
}
var storage = new LocalStorage(Environment.GetEnvironmentVariable("SRI_OUTPUT_PATH")
    ?? throw new InvalidOperationException("Configure SRI_OUTPUT_PATH; en Docker debe ser /app/downloads."));
storage.CheckAccess();
Console.WriteLine("Almacenamiento disponible.");
if (args.Contains("--self-test"))
{
    StorageChecks.Run(storage);
    return;
}
if (args.Contains("--inspect")) { await SriPortal.Inspect("inspection", CancellationToken.None); return; }
var statePath = Environment.GetEnvironmentVariable("SRI_STATE_PATH") ?? Path.GetFullPath("storage");
var builder = Host.CreateApplicationBuilder(args);
if (string.Equals(Environment.GetEnvironmentVariable("SRI_WORKER_PAUSED"), "true", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Worker en pausa: no procesa tareas ni autenticaciones.");
    await builder.Build().RunAsync();
    return;
}
EncryptionKey.Validate("SRI_SESSION_KEY_BASE64", Environment.GetEnvironmentVariable("SRI_SESSION_KEY_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation", "secrets", "session.key"));
builder.Services.AddSingleton(new WorkerSettings(statePath));
var repository = new JsonStateRepository(statePath);
var provider = new DocumentStorage(Environment.GetEnvironmentVariable("SRI_OUTPUT_PATH")!,()=>repository.Read(s=>s.Settings.DownloadSubfolder));
builder.Services.AddSingleton(new JobProcessor(repository, provider, new SriPortal(repository, new DownloadPipeline(repository, provider, new XmlDocumentParser()))));
builder.Services.AddSingleton(new WorkerAuthentication(repository));
builder.Services.AddHostedService<SriBackgroundService>();
await builder.Build().RunAsync();
