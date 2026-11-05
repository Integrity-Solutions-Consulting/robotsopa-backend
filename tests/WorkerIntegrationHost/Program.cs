using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Infrastructure;
using SriAutomation.Worker;

if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: <test-directory> <job-id> [--fixture]");
var directory = Path.GetFullPath(args[0]);
var targetJob = args[1];
var cs = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ?? throw new InvalidOperationException("Connection required.");
var parsed = new NpgsqlConnectionStringBuilder(cs);
if (parsed.Host is not ("127.0.0.1" or "localhost") || !parsed.Database!.StartsWith("botsopa_test_", StringComparison.Ordinal) || !Directory.Exists(directory) || !targetJob.StartsWith("test-", StringComparison.Ordinal))
    throw new InvalidOperationException("Only localhost test databases and test jobs are allowed.");
var repository = new PostgresStateRepository(cs);
var storage = new DocumentStorage(Path.GetFullPath(directory));
var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton<IStateRepository>(repository);
builder.Services.AddSingleton(new WorkerSettings(directory));
builder.Services.AddSingleton(new WorkerAuthentication(repository));
builder.Services.AddSingleton<JobProcessor>(args.Length == 3 && args[2] == "--fixture"
    ? WorkerProcessing.Create(repository, storage, new SriPortal(),
        (owned, content) => { File.WriteAllText(Path.Combine(directory, $"fixture-owner-{Environment.ProcessId}.json"), JsonSerializer.Serialize(owned.Ownership)); return new ControlledPortal(owned, new DownloadPipeline(owned, storage, new XmlDocumentParser(), content)); }, true)
    : new ControlledProcessor(repository, storage, targetJob, directory));
builder.Services.AddHostedService<SriBackgroundService>();
await builder.Build().RunAsync();

sealed class ControlledProcessor(PostgresStateRepository repository, IStorageProvider storage, string targetJob, string directory)
    : JobProcessor(repository, storage, new SriPortal())
{
    public override async Task ProcessOwned(ExecutionOwnership ownership, CancellationToken cancellation)
    {
        if (ownership.JobId != targetJob) throw new InvalidOperationException("Unexpected job.");
        // The production service invokes the method again after user cancellation to persist Cancelled.
        if (cancellation == CancellationToken.None) { await base.ProcessOwned(ownership, cancellation); return; }
        var path = Path.Combine(directory, $"owner-{Environment.ProcessId}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { pid = Environment.ProcessId, ownership, at = DateTimeOffset.UtcNow }), cancellation);
        try
        {
            repository.UpdateOwned(ownership, s => { s.Executions.Single(e => e.Id == ownership.JobId).Checkpoint = "integration-checkpoint"; return true; });
            while (!File.Exists(Path.Combine(directory, "finish")))
            {
                var staleSignal = Path.Combine(directory, $"stale-{Environment.ProcessId}");
                if (File.Exists(staleSignal))
                {
                    try { repository.UpdateOwned(ownership, s => { s.Executions.Single(e => e.Id == ownership.JobId).Checkpoint = "stale-write"; return true; }); File.WriteAllText(Path.Combine(directory, $"stale-accepted-{Environment.ProcessId}"), "unexpected"); }
                    catch (InvalidOperationException) { File.WriteAllText(Path.Combine(directory, $"stale-rejected-{Environment.ProcessId}"), "rejected"); }
                    File.Delete(staleSignal);
                }
                await Task.Delay(100, cancellation);
            }
            await base.ProcessOwned(ownership, cancellation);
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"cancelled-{Environment.ProcessId}"), DateTimeOffset.UtcNow.ToString("O"));
            throw;
        }
    }
}
