using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

var connection = Environment.GetEnvironmentVariable("SRI_PROBE_DATABASE") ?? throw new InvalidOperationException("Falta SRI_PROBE_DATABASE.");
var output = Environment.GetEnvironmentVariable("SRI_PROBE_OUTPUT") ?? throw new InvalidOperationException("Falta SRI_PROBE_OUTPUT.");
var postgres = new PostgresStateRepository(connection);
if (args.Contains("--cancel-worker"))
{
    postgres.Update(s => { s.Executions.Single(e => e.Id == "test-two-workers").CancelRequested = true; return true; });
    return;
}
if (args.Contains("--seed-lease"))
{
    postgres.Update(s => { s.Executions.Add(new Execution { Id = "test-lease-loss", RequestedBy = "testadmin", Query = new Query("1790000000001", new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), ["Facturas"], "Stored"), Status = "Pending" }); return true; });
    return;
}
if (args.Contains("--seed-fixture"))
{
    postgres.Update(s => { s.Executions.Add(new Execution { Id = "test-playwright", RequestedBy = "testadmin", Query = new Query("1790000000001", new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 12), ["Facturas"], "Sri"), Status = "Pending" }); return true; });
    return;
}
if (args.Contains("--seed-interrupted"))
{
    postgres.Update(s => { s.Documents.RemoveAll(d => d.Account == "1790000000001" && d.Issued == new DateOnly(2026, 9, 12)); s.Executions.Add(new Execution { Id = "test-interrupted", RequestedBy = "testadmin", Query = new Query("1790000000001", new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 12), ["Facturas"], "Sri"), Status = "Pending" }); return true; });
    return;
}
if (args.Contains("--check-fixture") || args.Contains("--check-interrupted"))
{
    var jobId = args.Contains("--check-interrupted") ? "test-interrupted" : "test-playwright";
    var job = postgres.Read(s => s.Executions.Single(e => e.Id == jobId));
    var document = postgres.Read(s => s.Documents.SingleOrDefault(d => d.Account == "1790000000001" && d.Issued == new DateOnly(2026, 9, 12)));
    var queue = new RemoteDeliveryStore(connection).Pending("testadmin");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { job.Status, job.Message, job.Issues, job.Queries, job.Reports, Document = document, Queue = queue }));
    if (job.Status is not ("Completed" or "CompletedWithWarnings") || job.Reports.Count != 1 || document?.XmlPath is null || document.PdfPath is null || !File.Exists(Path.Combine(output, document.XmlPath)) || !File.Exists(Path.Combine(output, document.PdfPath)) || !File.Exists(Path.Combine(output, job.Reports[0].Path)) || !queue.Any(d => d.DocumentId == document.Id && d.Kind == "xml") || !queue.Any(d => d.DocumentId == document.Id && d.Kind == "pdf") || !queue.Any(d => d.Kind == "xlsx" && d.Sha256 == Path.GetFileNameWithoutExtension(job.Reports[0].Path))) throw new Exception("Faltan documentos o entregas confirmadas del fixture.");
    Console.WriteLine($"Fixture: XML={document.XmlPath}; PDF={document.PdfPath}; Excel={job.Reports[0].Path}; entregas XML/PDF/XLSX confirmadas en cola.");
    return;
}
var id = Guid.NewGuid().ToString("N");
const string account = "1790000000001";
postgres.Update(s => { s.Accounts.Add(new Account(account, "Prueba aislada", account)); s.Executions.Add(new Execution { Id = id, RequestedBy = "testadmin", Query = new Query(account, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), ["Facturas"]), Status = "Pending" }); return true; });
var coordinator = new PostgresExecutionCoordinator(connection);
var first = coordinator.Acquire(id, "probe-a", TimeSpan.FromSeconds(2)) ?? throw new Exception("No se adquirió el primer lease.");
var storage = new DocumentStorage(Path.GetFullPath(output));
var oldStore = new OwnedContentStore(storage, new OwnedStateRepository(postgres, first), first);
var original = oldStore.Publish("first"u8.ToArray(), "xml", (s, path, hash) => { s.Executions.Single(e => e.Id == id).Checkpoint = path; return path; });
await Task.Delay(TimeSpan.FromSeconds(3));
var second = coordinator.Acquire(id, "probe-b", TimeSpan.FromSeconds(30)) ?? throw new Exception("No se recuperó el lease.");
try
{
    oldStore.Publish("stale"u8.ToArray(), "xml", (s, path, hash) => { s.Executions.Single(e => e.Id == id).Checkpoint = path; return path; });
    throw new Exception("La publicación del propietario antiguo no fue rechazada.");
}
catch (InvalidOperationException) { }
var newStore = new OwnedContentStore(storage, new OwnedStateRepository(postgres, second), second);
// Only run this opt-in probe against a disposable database and output directory.
if (args.Contains("--move-collision"))
{
var collisionBytes = System.Text.Encoding.UTF8.GetBytes("synthetic-move-collision-" + Guid.NewGuid().ToString("N"));
var collisionHash = DownloadPipeline.Hash(collisionBytes);
var collisionTarget = storage.Resolve(Path.Combine(".objects", collisionHash[..2], collisionHash + ".xml"));
var collisionStage = "";
var collisionStore = new OwnedContentStore(storage, new OwnedStateRepository(postgres, second), second, stage => {
    collisionStage = stage;
    if (stage == "PublishMove") Directory.CreateDirectory(collisionTarget);
});
try
{
    collisionStore.Publish(collisionBytes, "xml", (s, path, hash) => path);
    throw new Exception("No se reprodujo el conflicto de destino.");
}
catch (IOException) when (collisionStage == "PublishMove") { }
if (!Directory.Exists(collisionTarget) || !Directory.EnumerateFiles(storage.Resolve(Path.Combine(".staging", id)), "*.tmp", SearchOption.AllDirectories).Any()) throw new Exception("No se conservó el temporal de la prueba.");
Directory.Delete(collisionTarget);
var collisionRecovered = newStore.Publish(collisionBytes, "xml", (s, path, hash) => path);
if (!File.ReadAllBytes(storage.Resolve(collisionRecovered)).AsSpan().SequenceEqual(collisionBytes)) throw new Exception("No se recuperó la publicación sintética.");
}
var current = newStore.Publish("second"u8.ToArray(), "xml", (s, path, hash) => { s.Executions.Single(e => e.Id == id).Checkpoint = path; return path; });
if (original == current || File.ReadAllText(storage.Resolve(original)) != "first" || postgres.Read(s => s.Executions.Single(e => e.Id == id).Checkpoint) != current) throw new Exception("La publicación no conservó el objeto anterior o el manifiesto.");
try
{
    newStore.Publish<string>("orphan"u8.ToArray(), "xml", (State s, string path, string hash) => { s.Executions.Single(e => e.Id == id).Checkpoint = path; throw new IOException("Interrupción simulada antes de confirmar."); });
    throw new Exception("No se simuló la interrupción.");
}
catch (IOException) { }
if (postgres.Read(s => s.Executions.Single(e => e.Id == id).Checkpoint) != current) throw new Exception("La transacción interrumpida fue visible.");
var recovered = newStore.Publish("orphan"u8.ToArray(), "xml", (s, path, hash) => { s.Executions.Single(e => e.Id == id).Checkpoint = path; return path; });
if (File.ReadAllText(storage.Resolve(recovered)) != "orphan") throw new Exception("No se reconcilió el objeto existente.");
var documentId = Guid.NewGuid().ToString("N");
newStore.Publish("queue-fixture"u8.ToArray(), "xml", (s, path, hash) => { s.Documents.Add(new Voucher { Id = documentId, Account = account, Type = "Facturas", AccessKey = "123", Issued = new DateOnly(2025, 1, 1), XmlPath = path, XmlSha256 = hash }); return true; });
var deliveries = new RemoteDeliveryStore(connection);
if (deliveries.Pending("testadmin").Count(x => x.DocumentId == documentId) != 1) throw new Exception("La entrega confirmada no se creó.");
new OwnedStateRepository(postgres, second).Update(s => true);
if (deliveries.Pending("testadmin").Count(x => x.DocumentId == documentId) != 1) throw new Exception("La entrega se duplicó.");
var storedId = Guid.NewGuid().ToString("N");
postgres.Update(s => { s.Executions.Add(new Execution { Id = storedId, RequestedBy = "testadmin", Query = new Query(account, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), ["Facturas"], "Stored"), Status = "Pending" }); return true; });
var storedOwnership = coordinator.Acquire(storedId, "probe-report", TimeSpan.FromSeconds(30)) ?? throw new Exception("No se adquirió el trabajo del reporte.");
var processor = new JobProcessor(postgres, storage, new NoSri(), (_, _) => new NoSri());
await processor.ProcessOwned(storedOwnership, CancellationToken.None);
var storedJob = postgres.Read(s => s.Executions.Single(e => e.Id == storedId));
if (storedJob.Status is not ("Completed" or "CompletedWithWarnings") || storedJob.Reports.Count != 1 || !storedJob.Reports[0].Path.StartsWith(".objects")) throw new Exception("El procesador no confirmó el reporte inmutable.");
if (!deliveries.Pending("testadmin").Any(x => x.Kind == "xlsx" && x.AccountId == account)) throw new Exception("El reporte no creó su entrega.");
var cancelledId = Guid.NewGuid().ToString("N");
postgres.Update(s => { s.Executions.Add(new Execution { Id = cancelledId, Query = new Query(account, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), ["Facturas"], "Stored"), Status = "Pending" }); return true; });
var cancelLease = coordinator.Acquire(cancelledId, "probe-cancel", TimeSpan.FromSeconds(2)) ?? throw new Exception("No se adquirió el lease de cancelación.");
postgres.Update(s => { s.Executions.Single(e => e.Id == cancelledId).CancelRequested = true; return true; });
coordinator.FinalizeAbandonedCancellations();
if (postgres.Read(s => s.Executions.Single(e => e.Id == cancelledId).Status) != "Running") throw new Exception("Se canceló un lease todavía vigente.");
await Task.Delay(TimeSpan.FromSeconds(3));
coordinator.FinalizeAbandonedCancellations();
if (postgres.Read(s => s.Executions.Single(e => e.Id == cancelledId).Status) != "Cancelled" || coordinator.Renew(cancelLease, TimeSpan.FromSeconds(30))) throw new Exception("No se recuperó la cancelación abandonada.");
new OwnedStateRepository(postgres, second).Update(s => { s.Executions.Single(e => e.Id == id).Status = "Completed"; return true; });
coordinator.Release(second);
postgres.Update(s => { s.Executions.Add(new Execution { Id = "test-two-workers", RequestedBy = "testadmin", Query = new Query(account, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1), ["Facturas"], "Stored"), Status = "Pending" }); return true; });
Console.WriteLine("Lease vencido rechazado; publicación, rollback, reintento, reporte del procesador y cola verificados.");

sealed class NoSri : ISriComprobanteService
{
    public Task Execute(Query query, CancellationToken cancellation) => throw new InvalidOperationException("La prueba almacenada no debe consultar SRI.");
}
