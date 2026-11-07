using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

public static class RemoteExecutionEndpoints
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);

    public static void Map(WebApplication app, string connection, Func<HttpContext, string?> authenticate, Func<string, string, bool> canAccess, Func<CredentialVault>? vaultFactory = null)
    {
        var group = app.MapGroup("/execution-agent/v1");
        string? Identity(HttpContext c) => authenticate(c);
        ExecutionOwnership Owner(HttpContext c, string id)
        {
            if (!Guid.TryParse(c.Request.Headers["X-Delivery-Agent-Id"], out var agent) ||
                !Guid.TryParse(c.Request.Headers["X-Execution-Lease"], out var lease) ||
                !long.TryParse(c.Request.Headers["X-Execution-Generation"], out var generation) || generation < 1)
                throw new ArgumentException("Propiedad de ejecución inválida.");
            var user = Identity(c) ?? throw new ArgumentException("Identidad inválida.");
            var job = new PostgresStateRepository(connection).Read(s => s.Executions.Single(e => e.Id == id));
            if (!string.Equals(job.RequestedBy, user, StringComparison.OrdinalIgnoreCase) || !canAccess(user, job.Query.Account)) throw new ArgumentException("Cuenta no autorizada.");
            return new(Validation.Identifier(id), "remote-" + agent.ToString("N"), lease, generation);
        }
        var coordinator = new PostgresExecutionCoordinator(connection);
        var repository = new PostgresStateRepository(connection);
        group.MapPost("/claim", (HttpContext c) =>
        {
            var user = Identity(c); if (user is null) return Results.Unauthorized();
            coordinator.FinalizeAbandonedRemoteAttempts(user, 3);
            repository.Update(s => { s.AgentHeartbeat = DateTimeOffset.UtcNow; return true; });
            var agent = Guid.Parse(c.Request.Headers["X-Delivery-Agent-Id"]!);
            var candidates = repository.Read(s => s.Executions.Where(e => e.Query.Mode == "Sri" && !e.CancelRequested &&
                (e.Status is "Pending" or "Running") && e.Attempts < 3 && string.Equals(e.RequestedBy, user, StringComparison.OrdinalIgnoreCase) &&
                s.Accounts.Any(a => a.Id == e.Query.Account && !a.IsTest)).OrderBy(e => e.Created).Select(e => e.Id).ToList());
            foreach (var id in candidates)
            {
                var account = repository.Read(s => s.Executions.Single(e => e.Id == id).Query.Account);
                if (!canAccess(user, account)) continue;
                var owner = coordinator.Acquire(id, "remote-" + agent.ToString("N"), LeaseDuration);
                if (owner is not null) return Results.Ok(new { jobId = id, leaseId = owner.LeaseId, generation = owner.FencingToken });
            }
            return Results.NoContent();
        });
        group.MapPost("/{id}/renew", (HttpContext c, string id) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            var owner = Owner(c, id);
            if (!coordinator.Renew(owner, LeaseDuration)) return Results.Conflict();
            var cancelled = repository.UpdateOwned(owner, s => { s.AgentHeartbeat = DateTimeOffset.UtcNow; return s.Executions.Single(e => e.Id == id).CancelRequested; });
            return Results.Ok(new { cancelled });
        });
        group.MapPost("/{id}/credentials", async (HttpContext c, string id) =>
        {
            c.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            c.Response.Headers.Pragma = "no-cache";
            c.Response.Headers.XContentTypeOptions = "nosniff";
            if (!c.Request.IsHttps || c.Request.Headers.ContainsKey("Origin") || Identity(c) is null) { c.Response.StatusCode = 401; return; }
            var owner = Owner(c, id);
            var account = repository.UpdateOwned(owner, s => {
                var job = s.Executions.Single(e => e.Id == id);
                if (job.Status != "Running" || job.CancelRequested || job.Query.Mode != "Sri") throw new ArgumentException("Ejecución no autorizada.");
                return s.Accounts.Single(a => a.Id == job.Query.Account && !a.IsTest);
            });
            var taxId = account.TaxId ?? account.Id;
            var vault = vaultFactory?.Invoke() ?? CredentialVault.Configured();
            if (!vault.Present(account.Id)) { c.Response.StatusCode = 404; return; }
            await vault.Use(account.Id, taxId, async password => {
                // Recheck revocation, assignment, cancellation and the lease after decrypting.
                if (Identity(c) is null) { c.Response.StatusCode = 401; return; }
                _ = Owner(c, id);
                repository.UpdateOwned(owner, s => { var job = s.Executions.Single(e => e.Id == id); if (job.Status != "Running" || job.CancelRequested || job.Query.Account != account.Id) throw new ArgumentException("Ejecución detenida."); return true; });
                await c.Response.WriteAsJsonAsync(new { username = taxId, password }, c.RequestAborted);
            });
        });
        group.MapGet("/{id}/state", (HttpContext c, string id) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            var owner = Owner(c, id);
            var state = repository.UpdateOwned(owner, s => {
                var job = s.Executions.Single(e => e.Id == id);
                if (job.CancelRequested || job.Status != "Running") throw new ArgumentException("Ejecución detenida.");
                return new State { Executions = [job], Accounts = [s.Accounts.Single(a => a.Id == job.Query.Account)],
                    Documents = s.Documents.Where(d => d.Account == job.Query.Account && d.Issued >= job.Query.From && d.Issued <= job.Query.To && job.Query.Types.Contains(d.Type)).ToList() };
            });
            return Results.Ok(state);
        });
        group.MapPut("/{id}/state", (HttpContext c, string id, State delta) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            repository.UpdateOwned(Owner(c, id), s => { NativeExecution.ApplyRemote(s, id, delta); return true; });
            return Results.Ok(new { saved = true });
        });
        group.MapGet("/{id}/available/{document}/{extension}", (HttpContext c, string id, string document, string extension, IStorageProvider storage, IDocumentParser parser) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            if (extension is not ("xml" or "pdf")) throw new ArgumentException("Formato inválido.");
            var owner = Owner(c, id); var scoped = new OwnedStateRepository(repository, owner); scoped.VerifyOwnership();
            var doc = scoped.Read(s => { var job = s.Executions.Single(e => e.Id == id); return s.Documents.Single(d => d.Id == document && d.Account == job.Query.Account); });
            return Results.Ok(new DownloadPipeline(scoped, storage, parser, new OwnedContentStore(storage, scoped, owner)).IsAvailable(doc, extension));
        });
        group.MapPost("/{id}/files", (HttpContext c, string id, DownloadedVoucher file, IStorageProvider storage, IDocumentParser parser) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            var owner = Owner(c, id); var scoped = new OwnedStateRepository(repository, owner);
            try
            {
            scoped.Update(s => { var job = s.Executions.Single(e => e.Id == id);
                if (job.Status != "Running" || job.CancelRequested || file.Account != job.Query.Account || file.Issued < job.Query.From || file.Issued > job.Query.To || !job.Query.Types.Contains(file.Type) || !job.AttemptedKeys.Contains(file.Key)) throw new RemoteFileScopeException();
                return true; });
            var result = new DownloadPipeline(scoped, storage, parser, new OwnedContentStore(storage, scoped, owner)).Process(file);
            try { scoped.Update(s => { var job = s.Executions.Single(e => e.Id == id); if (job.Events.LastOrDefault()?.Stage == "RemoteFileError") job.Events.Add(new(DateTimeOffset.UtcNow, "RemoteFileAccepted", "Archivo aceptado.")); return true; }); }
            catch (Exception) { /* The file was already committed; do not turn its acknowledgement into a failure. */ }
            return Results.Ok(result);
            }
            catch (Exception ex) when (ex is not ExecutionLeaseException)
            {
                var code = ex is IOException or UnauthorizedAccessException ? "STORAGE_UNAVAILABLE" : ex is ArgumentException or InvalidDataException or System.Xml.XmlException ? "DOCUMENT_OR_REQUEST_INVALID" : "INTERNAL_ERROR";
                try { scoped.Update(s => { var job = s.Executions.Single(e => e.Id == id); job.Events.Add(new(DateTimeOffset.UtcNow, "RemoteFileError", $"{owner.FencingToken}:{code}")); if (job.Events.Count > 100) job.Events.RemoveAt(0); return true; }); }
                catch (Exception) { /* Preserve the original error if the lease or database is unavailable. */ }
                throw;
            }
        });
        group.MapPost("/{id}/finish", async (HttpContext c, string id, IStorageProvider storage) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            var owner = Owner(c, id);
            repository.UpdateOwned(owner, s => { var job = s.Executions.Single(e => e.Id == id); if (job.CancelRequested || job.Status != "Running" || job.SriCheckpoint?.Complete != true) throw new ArgumentException("Consulta no finalizada."); return true; });
            var processor = new JobProcessor(repository, storage, new CompletedQuery(), (_, _) => new CompletedQuery(), true);
            await processor.ProcessOwned(owner, c.RequestAborted);
            var status = repository.Read(s => s.Executions.Single(e => e.Id == id).Status);
            coordinator.Release(owner);
            return Results.Ok(new { status });
        });
        group.MapPost("/{id}/stop", (HttpContext c, string id) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            var owner = Owner(c, id);
            var challenge = c.Request.Headers["X-Stop-Reason"] == "HumanChallenge";
            var fileFailure = repository.UpdateOwned(owner, s => { var job = s.Executions.Single(e => e.Id == id); if (job.Status != "Running") return true; var last = job.Events.LastOrDefault(); if (!challenge && !job.CancelRequested && last?.Stage == "RemoteFileError" && last.Message.StartsWith($"{owner.FencingToken}:", StringComparison.Ordinal)) { job.Status = "Failed"; job.Errors++; job.Finished = DateTimeOffset.UtcNow; ExecutionProgress.Set(s, id, "Failed", "La API rechazó o no pudo almacenar el documento. Revise los registros del backend y reintente desde Historial."); AuditTrail.Add(s, job.RequestedBy ?? "Agente", "Resultado de procesamiento", job.Query.Account, job.Status); return true; } return false; });
            if (fileFailure) { coordinator.Release(owner); return Results.Ok(); }
            repository.UpdateOwned(owner, s => { var job = s.Executions.Single(e => e.Id == id); job.Status = job.CancelRequested ? "Cancelled" : "RequiresHumanAction"; job.Stage = job.Status; job.Finished = DateTimeOffset.UtcNow; ExecutionProgress.Set(s, id, job.Status, job.CancelRequested ? "Cancelada por el usuario." : challenge ? "El SRI solicita CAPTCHA o MFA. Complete la verificación autorizada y reanude el trabajo." : "La ejecución se detuvo y requiere intervención en Windows."); AuditTrail.Add(s, job.RequestedBy ?? "Agente", "Resultado de procesamiento", job.Query.Account, job.Status); return true; });
            coordinator.Release(owner); return Results.Ok();
        });
    }
    private sealed class CompletedQuery : ISriComprobanteService
    {
        public Task Execute(Query query, CancellationToken cancellation) => Task.CompletedTask;
    }
}

public sealed class RemoteFileScopeException() : ArgumentException("Archivo fuera del trabajo autorizado.");
