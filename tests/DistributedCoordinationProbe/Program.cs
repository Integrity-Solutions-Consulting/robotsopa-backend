using System.Text.Json;
using SriAutomation.Infrastructure;

// Standalone integration executable. It is not referenced by API, Worker or their images.
if (args.Length != 5) throw new ArgumentException("Usage: <connection-string-env> <job-id> <worker-id> <barrier-directory> <duration-seconds>");
var cs = Environment.GetEnvironmentVariable(args[0]) ?? throw new InvalidOperationException("Missing test connection string.");
var job = args[1]; var worker = args[2]; var barrier = Path.GetFullPath(args[3]);
var seconds = int.Parse(args[4]);
if (!Directory.Exists(barrier) || seconds is < 1 or > 300) throw new ArgumentException("Invalid probe arguments.");
void Log(string evt, object? value = null) => Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, worker, job, evt, value }));
File.WriteAllText(Path.Combine(barrier, worker + ".ready"), "ready");
while (!File.Exists(Path.Combine(barrier, "go"))) await Task.Delay(20);
var coordinator = new PostgresExecutionCoordinator(cs);
var ownership = coordinator.Acquire(job, worker, TimeSpan.FromSeconds(4));
Log("acquire", ownership);
if (ownership is null) return;
var repo = new PostgresStateRepository(cs);
var end = DateTimeOffset.UtcNow.AddSeconds(seconds);
while (DateTimeOffset.UtcNow < end)
{
    await Task.Delay(1000);
    if (File.Exists(Path.Combine(barrier, worker + ".fail-renew"))) { Log("renewal-failure"); break; }
    if (!coordinator.Renew(ownership, TimeSpan.FromSeconds(4))) { Log("lost"); break; }
    Log("renewed", ownership.FencingToken);
}
Log("stopped");
while (!File.Exists(Path.Combine(barrier, worker + ".finish"))) await Task.Delay(50);
foreach (var field in new[] { "progress", "checkpoint", "status" })
{
    try
    {
        repo.UpdateOwned(ownership, s => { var e = s.Executions.Single(x => x.Id == job); if (field == "progress") e.Processed++; else if (field == "checkpoint") e.Checkpoint = "probe"; else e.Status = "RequiresHumanAction"; return true; });
        Log("write-accepted", field);
    }
    catch (InvalidOperationException) { Log("write-rejected", field); }
}
Log("release", coordinator.Release(ownership));
