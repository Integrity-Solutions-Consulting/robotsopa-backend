using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using Npgsql;

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
        group.MapGet("/capabilities", (HttpContext c) => Identity(c) is null
            ? Results.Unauthorized() : Results.Ok(new { centralStorage = "v1", conditionalPending = true }));
        group.MapGet("/{id}/confirmed", (HttpContext c, string id, string account, string type, string key, DateOnly issued, string extension, long generation, IStorageProvider storage, IDocumentParser parser) =>
        {
            var user = Identity(c); if (user is null) return Results.Unauthorized();
            if (extension is not ("pdf" or "xml") || generation < 1 || key.Length != 49 || !key.All(char.IsAsciiDigit) || !canAccess(user, account)) return Results.StatusCode(403);
            var candidate = repository.Read(s => {
                var job = s.Executions.SingleOrDefault(e => e.Id == id);
                if (job is null || !string.Equals(job.RequestedBy, user, StringComparison.OrdinalIgnoreCase) || !job.CentralStorageAgent ||
                    job.Status is not ("Completed" or "CompletedWithWarnings") || job.CancelRequested || job.Query.Account != account ||
                    issued < job.Query.From || issued > job.Query.To || !job.Query.Types.Contains(type) || !job.AttemptedKeys.Contains(key) ||
                    (extension == "pdf" && !job.Query.Pdf) || (extension == "xml" && !job.Query.Xml)) return null;
                return s.Documents.SingleOrDefault(d => d.Account == account && d.Type == type && d.AccessKey == key && d.Issued == issued);
            });
            if (candidate is null) return Results.NotFound();
            using (var db = new NpgsqlConnection(connection))
            {
                db.Open(); using var cmd = new NpgsqlCommand("SELECT fencing_token FROM execution_ownership WHERE job_id=@id AND coordination_state='released'", db);
                cmd.Parameters.AddWithValue("id", id);
                if (cmd.ExecuteScalar() is not long current || generation > current) return Results.NotFound();
            }
            if (!new DownloadPipeline(repository, storage, parser).IsAvailable(candidate, extension)) return Results.NotFound();
            var hash = extension == "pdf" ? candidate.PdfSha256 : candidate.XmlSha256;
            var unchanged = repository.Read(s => {
                var job = s.Executions.SingleOrDefault(e => e.Id == id);
                var doc = s.Documents.SingleOrDefault(d => d.Id == candidate.Id && d.Account == account);
                return job is not null && (job.Status is "Completed" or "CompletedWithWarnings") && !job.CancelRequested &&
                    job.CentralStorageAgent && string.Equals(job.RequestedBy, user, StringComparison.OrdinalIgnoreCase) &&
                    doc is not null && (extension == "pdf" ? doc.PdfSha256 == hash && doc.PdfPath == candidate.PdfPath : doc.XmlSha256 == hash && doc.XmlPath == candidate.XmlPath);
            });
            if (!unchanged || !canAccess(user, account)) return Results.NotFound();
            c.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { sha256 = hash });
        });
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
                if (owner is not null)
                {
                    var central = c.Request.Headers["X-SRI-Storage-Mode"] == "central-v1";
                    repository.UpdateOwned(owner, s => { s.Executions.Single(e => e.Id == id).CentralStorageAgent = central; return true; });
                    return Results.Ok(new { jobId = id, leaseId = owner.LeaseId, generation = owner.FencingToken, storageMode = central ? "central-v1" : "legacy" });
                }
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
        group.MapGet("/{id}/central-available/{document}/{extension}", (HttpContext c, string id, string document, string extension, IStorageProvider storage, IDocumentParser parser) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            if (extension is not ("xml" or "pdf")) throw new ArgumentException("Formato inválido.");
            var owner = Owner(c, id); var scoped = new OwnedStateRepository(repository, owner);
            var doc = scoped.Update(s => {
                var job = s.Executions.Single(e => e.Id == id);
                if (!job.CentralStorageAgent || job.Status != "Running" || job.CancelRequested) throw new ArgumentException("Modo o ejecución no autorizados.");
                return s.Documents.Single(d => d.Id == document && d.Account == job.Query.Account && d.Issued >= job.Query.From && d.Issued <= job.Query.To && job.Query.Types.Contains(d.Type));
            });
            var available = new DownloadPipeline(scoped, storage, parser, new OwnedContentStore(storage, scoped, owner)).IsAvailable(doc, extension);
            scoped.Update(s => {
                var job = s.Executions.Single(e => e.Id == id);
                if (!job.CentralStorageAgent || job.Status != "Running" || job.CancelRequested) throw new ArgumentException("Ejecución detenida.");
                var current = s.Documents.Single(d => d.Id == document && d.Account == job.Query.Account);
                if ((extension == "xml" ? current.XmlPath : current.PdfPath) != (extension == "xml" ? doc.XmlPath : doc.PdfPath) ||
                    (extension == "xml" ? current.XmlSha256 : current.PdfSha256) != (extension == "xml" ? doc.XmlSha256 : doc.PdfSha256)) available = false;
                if (available) { if (!job.AttemptedKeys.Contains(doc.AccessKey)) job.AttemptedKeys.Add(doc.AccessKey); ReviewCounter.Update(s, job); }
                return true;
            });
            c.Response.Headers.CacheControl = "no-store";
            if (available) c.Response.Headers["X-SRI-Content-SHA256"] = extension == "xml" ? doc.XmlSha256 : doc.PdfSha256;
            return Results.Ok(available);
        });
        group.MapGet("/{id}/available/{document}/{extension}", (HttpContext c, string id, string document, string extension, IStorageProvider storage, IDocumentParser parser) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            if (extension is not ("xml" or "pdf")) throw new ArgumentException("Formato inválido.");
            var owner = Owner(c, id); var scoped = new OwnedStateRepository(repository, owner); scoped.VerifyOwnership();
            var doc = scoped.Read(s => { var job = s.Executions.Single(e => e.Id == id); return s.Documents.Single(d => d.Id == document && d.Account == job.Query.Account); });
            var available = new DownloadPipeline(scoped, storage, parser, new OwnedContentStore(storage, scoped, owner)).IsAvailable(doc, extension);
            if (available)
            {
                // UpdateOwned also creates a delivery if one does not yet exist for this user.
                scoped.Update(s => {
                    var job = s.Executions.Single(e => e.Id == id);
                    if (doc.Issued >= job.Query.From && doc.Issued <= job.Query.To && job.Query.Types.Contains(doc.Type))
                    {
                        if (!job.AttemptedKeys.Contains(doc.AccessKey)) job.AttemptedKeys.Add(doc.AccessKey);
                        ReviewCounter.Update(s, job);
                    }
                    return true;
                });
                var hash = extension == "xml" ? doc.XmlSha256 : doc.PdfSha256;
                if (hash is not null) new RemoteDeliveryStore(connection).Requeue(Identity(c)!, doc.Id, extension, hash);
            }
            return Results.Ok(available);
        });
        group.MapPost("/{id}/files", (HttpContext c, string id, DownloadedVoucher file, IStorageProvider storage, IDocumentParser parser) =>
        {
            if (Identity(c) is null) return Results.Unauthorized();
            var stage = "Ownership";
            string? rejectionCode = null;
            try
            {
                var owner = Owner(c, id); var scoped = new OwnedStateRepository(repository, owner);
                stage = "Authorization";
                scoped.Update(s => { var job = s.Executions.Single(e => e.Id == id);
                    rejectionCode = job.Status != "Running" || job.CancelRequested ? "JOB_NOT_RUNNING" :
                        file.Account != job.Query.Account ? "JOB_ACCOUNT_MISMATCH" :
                        job.CentralStorageAgent && (file.Pdf is not null && !job.Query.Pdf || file.Xml is not null && !job.Query.Xml) ? "JOB_FORMAT_MISMATCH" :
                        file.Issued < job.Query.From || file.Issued > job.Query.To ? "JOB_DATE_OUT_OF_RANGE" :
                        !job.Query.Types.Contains(file.Type) ? "JOB_TYPE_MISMATCH" :
                        !job.AttemptedKeys.Contains(file.Key) ? "JOB_KEY_NOT_ATTEMPTED" : null;
                    if (rejectionCode is not null) throw new ArgumentException("Archivo fuera del trabajo autorizado.");
                    return true; });
                var guarded = c.Request.Headers["X-SRI-Pending-Only-If-Missing-Or-Same"] == "1";
                var document = new DownloadPipeline(scoped, storage, parser, new OwnedContentStore(storage, scoped, owner, value => stage = value), value => stage = value, guarded).Process(file);
                stage = "DeliveryRequeue";
                var deliveries = new RemoteDeliveryStore(connection);
                var central = scoped.Update(s => s.Executions.Single(e => e.Id == id).CentralStorageAgent);
                if (!central && file.Xml is not null && document.XmlSha256 is not null) deliveries.Requeue(Identity(c)!, document.Id, "xml", document.XmlSha256);
                if (!central && file.Pdf is not null && document.PdfSha256 is not null) deliveries.Requeue(Identity(c)!, document.Id, "pdf", document.PdfSha256);
                return Results.Ok(document);
            }
            catch (Exception ex)
            {
                var category = ex is InvalidOperationException && ex.Message.StartsWith("Execution ownership", StringComparison.Ordinal) ? "Lease" : "Other";
                c.Items["FileRejectionLogged"] = true;
                app.Logger.LogWarning("Remote file failed: stage={Stage} category={Category} type={Type} code={Code} hresult={HResult}", stage, category, ex.GetType().Name, rejectionCode ?? FileRejectionDiagnostics.Code(stage, ex), ex is IOException ? ex.HResult : (int?)null);
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
            try
            {
            var owner = Owner(c, id);
            var challenge = c.Request.Headers["X-Stop-Reason"] == "HumanChallenge";
            repository.UpdateOwned(owner, s => { var job = s.Executions.Single(e => e.Id == id); job.Status = job.CancelRequested ? "Cancelled" : "RequiresHumanAction"; job.Stage = job.Status; job.Finished = DateTimeOffset.UtcNow; ExecutionProgress.Set(s, id, job.Status, job.CancelRequested ? "Cancelada por el usuario." : challenge ? "El SRI solicita CAPTCHA o MFA. Complete la verificación autorizada y reanude el trabajo." : "La ejecución se detuvo y requiere intervención en Windows."); AuditTrail.Add(s, job.RequestedBy ?? "Agente", "Resultado de procesamiento", job.Query.Account, job.Status); return true; });
            coordinator.Release(owner); return Results.Ok();
            }
            catch (InvalidOperationException) { return Results.Conflict(new { code = "LeaseExpired", message = "El lease ya no es vÃ¡lido; no se aplicÃ³ ninguna escritura del agente antiguo." }); }
        });
    }
    private sealed class CompletedQuery : ISriComprobanteService
    {
        public Task Execute(Query query, CancellationToken cancellation) => Task.CompletedTask;
    }
}
