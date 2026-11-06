using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.Api;

var builder = WebApplication.CreateBuilder(args.Where(arg => arg is not "--db-migrate" and not "--db-init").ToArray());
var trustedProxies = (Environment.GetEnvironmentVariable("SRI_TRUSTED_PROXY_IPS") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
var trustedNetworks = (Environment.GetEnvironmentVariable("SRI_TRUSTED_PROXY_NETWORKS") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (trustedProxies.Length > 0 || trustedNetworks.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options => TrustedProxyConfiguration.Configure(options, trustedProxies, trustedNetworks));
}
var postgresConnection = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(postgresConnection)) postgresConnection = null;
if (string.IsNullOrWhiteSpace(postgresConnection) && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("Configure ConnectionStrings__DefaultConnection.");
if (args.Contains("--db-migrate")) { PostgresAdministration.Migrate(postgresConnection ?? throw new InvalidOperationException("Configure ConnectionStrings__DefaultConnection.")); return; }
if (args.Contains("--db-init")) { PostgresAdministration.Initialize(postgresConnection ?? throw new InvalidOperationException("Configure ConnectionStrings__DefaultConnection.")); return; }
var secureCookie = string.Equals(Environment.GetEnvironmentVariable("SRI_COOKIE_SECURE"), "true", StringComparison.OrdinalIgnoreCase);
var password = Environment.GetEnvironmentVariable("SRI_ADMIN_PASSWORD");
if (postgresConnection is null && (password is null || password.Length < 16)) throw new InvalidOperationException("Configure SRI_ADMIN_PASSWORD con al menos 16 caracteres.");
var output = Environment.GetEnvironmentVariable("SRI_OUTPUT_PATH") ?? Path.GetFullPath("downloads");
var adminUsername = Environment.GetEnvironmentVariable("SRI_ADMIN_USERNAME");
if (postgresConnection is null && string.IsNullOrWhiteSpace(adminUsername)) throw new InvalidOperationException("Configure SRI_ADMIN_USERNAME con la identidad del administrador.");
var credentialKeyFile = Environment.GetEnvironmentVariable("SRI_CREDENTIAL_KEY_FILE");
if (Environment.GetEnvironmentVariable("SRI_CREDENTIAL_KEY_BASE64") is not null || credentialKeyFile is not null)
    EncryptionKey.Validate("SRI_CREDENTIAL_KEY_BASE64", credentialKeyFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation", "secrets", "credential.key"));
builder.Services.AddSingleton<IStateRepository>(_ => postgresConnection is null ? new JsonStateRepository(Environment.GetEnvironmentVariable("SRI_STATE_PATH") ?? Path.GetFullPath("storage")) : new PostgresStateRepository(postgresConnection));
builder.Services.AddSingleton<IPanelDatabase>(_ => postgresConnection is null ? new PanelDatabase(Environment.GetEnvironmentVariable("SRI_STATE_PATH") ?? Path.GetFullPath("storage")) : new PostgresPanelDatabase(postgresConnection));
builder.Services.AddSingleton<IStorageProvider>(services=>new DocumentStorage(output,()=>services.GetRequiredService<IStateRepository>().Read(s=>s.Settings.DownloadSubfolder)));
builder.Services.AddSingleton<IDocumentParser, XmlDocumentParser>();
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(); builder.Services.AddHealthChecks().AddCheck<SriAutomation.Api.StorageHealthCheck>("storage");
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("login", x => { x.PermitLimit = 10; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; }));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20_000_000);
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
var app = builder.Build();
if (trustedProxies.Length > 0 || trustedNetworks.Length > 0) app.UseForwardedHeaders();
app.UseCors();
var database = app.Services.GetRequiredService<IPanelDatabase>();
if (postgresConnection is null) { database.Initialize(adminUsername!, password!); database.SyncAudit(app.Services.GetRequiredService<IStateRepository>().Read(s => s.Audit.ToList())); database.SyncAccounts(app.Services.GetRequiredService<IStateRepository>().Read(s => s.Accounts.ToList())); }
else PostgresAdministration.Verify(postgresConnection);
var sessions = new ConcurrentDictionary<string, (DateTimeOffset Expiry, string User, string Role, bool MustChange)>();
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (context.Request.Method is not "GET" and not "HEAD" && context.Request.Headers.Origin.FirstOrDefault() is { } origin &&
                (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Authority != context.Request.Host.Value && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)))) { DeliveryRegistrationDiagnostics.Rejected(context, app.Logger, "ORIGIN_UNAUTHORIZED"); context.Response.StatusCode = 403; return; }
            if (context.Request.Path != "/api/v1/auth/login")
            {
                var token = context.Request.Cookies["sri-session"];
                if (token is null || !sessions.TryGetValue(token, out var session) || session.Expiry <= DateTimeOffset.UtcNow) { DeliveryRegistrationDiagnostics.Rejected(context, app.Logger, "SESSION_INVALID"); context.Response.StatusCode = 401; return; }
                context.Items["PanelUser"] = session.User;
                context.Items["PanelRole"] = session.Role;
                if (session.Role == "asistente" && !await AssistantAccess.Allowed(context, database, app.Services.GetRequiredService<IStateRepository>())) { DeliveryRegistrationDiagnostics.Rejected(context, app.Logger, "ROLE_UNAUTHORIZED"); context.Response.StatusCode = 403; return; }
                if (session.MustChange && context.Request.Path != "/api/v1/auth/session" && context.Request.Path != "/api/v1/auth/change-password" && context.Request.Path != "/api/v1/auth/logout") { DeliveryRegistrationDiagnostics.Rejected(context, app.Logger, "PASSWORD_CHANGE_REQUIRED"); context.Response.StatusCode = 403; return; }
            }
        }
        await next();
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or KeyNotFoundException or System.Xml.XmlException)
    { await Results.Problem(ex.Message, statusCode: ex is KeyNotFoundException ? 404 : 400).ExecuteAsync(context); }
    catch (BadHttpRequestException) { await Results.Problem("Solicitud inválida. Revise los campos admitidos.",statusCode:400).ExecuteAsync(context); }
    catch (IOException) { await Results.Problem("Almacenamiento ocupado o no disponible. Reintente.", statusCode: 503).ExecuteAsync(context); }
    catch (Exception ex) { app.Logger.LogError("Error interno de tipo {Type}", ex.GetType().Name); await Results.Problem("Error interno.", statusCode: 500).ExecuteAsync(context); }
    finally { AuditEndpoints.RecordRequest(context, app.Services.GetRequiredService<IStateRepository>()); }
});
app.UseRateLimiter(); if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); } app.MapHealthChecks("/health");
app.MapGet("/health/ready", () => { try { if (postgresConnection is null) return Results.StatusCode(503); PostgresAdministration.Verify(postgresConnection); return Results.Ok(); } catch { return Results.StatusCode(503); } });
if (postgresConnection is not null)
    app.Services.GetRequiredService<IStateRepository>().Read(s => {
        if (s.Accounts.Any(a => !a.IsTest && a.Id.StartsWith("qa-", StringComparison.Ordinal) && a.Name.StartsWith("QA FICTICIA", StringComparison.Ordinal)))
            throw new InvalidOperationException("El estado contiene cuentas de prueba antiguas sin marcar; revise su clasificación antes de iniciar.");
        if (s.AuthenticationMode != "EncryptedProfilesV4" && (s.Authentications.Count > 0 || s.Executions.Any(e => e.Query.Mode == "Sri" && e.Status is "Pending" or "Running")))
            throw new InvalidOperationException("El estado contiene autenticaciones o trabajos SRI antiguos que requieren revisión segura antes de iniciar.");
        return true;
    });
else app.Services.GetRequiredService<IStateRepository>().Update(s => { DataScope.MarkLegacyFixtures(s);
if(s.AuthenticationMode!="EncryptedProfilesV4") {
 foreach(var op in s.Authentications){op.SessionVerified=false;op.Expires=null;op.Command="";op.Status="Cancelled";op.BrowserOpened=false;}
 foreach(var job in s.Executions.Where(e=>e.Query.Mode=="Sri"&&e.Status is "Pending" or "Running")){job.Status=job.Stage="RequiresHumanAction";job.Message="Flujo de acceso actualizado. Confirme la reanudación desde Angular.";}
 s.AuthenticationMode="EncryptedProfilesV4";
} return true; });
var api = app.MapGroup("/api/v1");
if (postgresConnection is not null)
{
    var agentCredentials = new DeliveryAgentCredentials(postgresConnection);
    var remoteDeliveries = new RemoteDeliveryStore(postgresConnection);
    api.MapGet("/delivery-agents", (HttpContext context) => {
        context.Response.Headers.CacheControl = "no-store";
        var enabled = string.Equals(Environment.GetEnvironmentVariable("SRI_REMOTE_EXECUTION_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow.AddSeconds(-30);
        return Results.Ok(agentCredentials.List((string)context.Items["PanelUser"]!).Select(a => new {
            a.Id, a.Label, a.CreatedAt, a.RevokedAt, a.LastSeenAt,
            executionAvailable = enabled, online = enabled && a.RevokedAt is null && a.LastSeenAt > cutoff
        }));
    });
    api.MapPost("/delivery-agents", (AgentRegistration input, HttpContext context) =>
    {
        if (!context.Request.IsHttps) { DeliveryRegistrationDiagnostics.Rejected(context, app.Logger, "HTTPS_NOT_RECOGNIZED"); return Results.Problem("Se requiere HTTPS. Si usa un proxy inverso, configure su IP o red confiable y X-Forwarded-Proto.", statusCode: 403); }
        var created = agentCredentials.Register((string)context.Items["PanelUser"]!, input.Label);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { id = created.Id, secret = created.Secret });
    });
    api.MapPost("/delivery-agents/{id:guid}/rotate", (Guid id, HttpContext context) =>
    {
        if (!context.Request.IsHttps) return Results.Problem("Se requiere HTTPS. Si usa un proxy inverso, configure su IP o red confiable y X-Forwarded-Proto.", statusCode: 403);
        context.Response.Headers.CacheControl = "no-store";
        var secret = agentCredentials.Rotate(id, (string)context.Items["PanelUser"]!);
        return secret is null ? Results.NotFound() : Results.Ok(new { id, secret });
    });
    api.MapDelete("/delivery-agents/{id:guid}", (Guid id, HttpContext context) =>
        agentCredentials.Revoke(id, (string)context.Items["PanelUser"]!) ? Results.NoContent() : Results.NotFound());
    api.MapGet("/delivery-queue", (HttpContext context) => { context.Response.Headers.CacheControl = "no-store"; return Results.Ok(remoteDeliveries.Recent((string)context.Items["PanelUser"]!)); });
    api.MapPost("/delivery-queue/{documentId}/{kind}", (string documentId, string kind, HttpContext context, IStateRepository state, IStorageProvider storage) =>
    {
        if (!context.Request.IsHttps || kind is not ("xml" or "pdf")) return Results.BadRequest();
        var document = state.Read(s => s.Documents.SingleOrDefault(d => d.Id == documentId));
        if (document is null || !Allowed(context, database, document.Account)) return Results.NotFound();
        var account = state.Read(s => s.Accounts.SingleOrDefault(a => a.Id == document.Account));
        var taxId = account?.TaxId;
        if (taxId is null || taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) return Results.BadRequest();
        var source = kind == "xml" ? document.XmlPath : document.PdfPath;
        var hash = kind == "xml" ? document.XmlSha256 : document.PdfSha256;
        if (source is null || hash is null || !Validation.Types.Contains(document.Type)) return Results.NotFound();
        var file = storage.Resolve(source);
        if (!File.Exists(file) || DownloadPipeline.Hash(File.ReadAllBytes(file)) != hash) return Results.Conflict();
        var destination = string.Join('/', taxId, document.Issued.Year.ToString("D4"), document.Issued.Month.ToString("D2"), document.Type, Validation.Identifier(document.AccessKey) + "." + kind);
        remoteDeliveries.Enqueue((string)context.Items["PanelUser"]!, document.Account, document.Id, kind, source, destination, hash);
        return Results.Ok(new { queued = true });
    });

    string? AgentUser(HttpContext context)
    {
        if (!context.Request.IsHttps || context.Request.Headers.ContainsKey("Origin")) return null;
        if (!Guid.TryParse(context.Request.Headers["X-Delivery-Agent-Id"].ToString(), out var id)) return null;
        var user = agentCredentials.Authenticate(id, context.Request.Headers["X-Delivery-Agent-Secret"].ToString());
        if (user is not null && context.Request.Method == "POST" &&
            (context.Request.Path == "/execution-agent/v1/claim" || context.Request.Path.Value?.EndsWith("/renew", StringComparison.Ordinal) == true && context.Request.Path.StartsWithSegments("/execution-agent/v1")))
            agentCredentials.RecordExecutionActivity(id, user);
        return user;
    }
    var remoteExecutionEnabled = string.Equals(Environment.GetEnvironmentVariable("SRI_REMOTE_EXECUTION_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
    RemoteAgentStatus.Map(app, AgentUser, remoteExecutionEnabled);
    if (remoteExecutionEnabled)
        RemoteExecutionEndpoints.Map(app, postgresConnection, AgentUser, agentCredentials.CanAccess);
    app.MapGet("/delivery-agent/v1/pending", (HttpContext context) =>
    {
        var username = AgentUser(context);
        return username is null ? Results.Unauthorized() : Results.Ok(remoteDeliveries.Pending(username));
    });
    app.MapGet("/delivery-agent/v1/{id:guid}/file", (Guid id, HttpContext context, IStateRepository state, IStorageProvider storage) =>
    {
        var username = AgentUser(context);
        if (username is null) return Results.Unauthorized();
        var item = remoteDeliveries.GetFile(username, id);
        if (item is null || item.Kind is not ("xml" or "pdf" or "xlsx")) return Results.NotFound();
        var document = item.Kind == "xlsx" ? null : state.Read(s => s.Documents.SingleOrDefault(d => d.Id == item.DocumentId && d.Account == item.AccountId));
        var path = item.Kind == "xlsx" ? item.SourcePath : item.Kind == "xml" ? document?.XmlPath : document?.PdfPath;
        var hash = item.Kind == "xlsx" ? item.Sha256 : item.Kind == "xml" ? document?.XmlSha256 : document?.PdfSha256;
        if (path is null || path != item.SourcePath || hash != item.Sha256) return Results.NotFound();
        var resolved = storage.Resolve(path);
        if (!File.Exists(resolved)) return Results.NotFound();
        var bytes = File.ReadAllBytes(resolved);
        if (!string.Equals(DownloadPipeline.Hash(bytes), item.Sha256, StringComparison.Ordinal)) return Results.Conflict();
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(bytes, "application/octet-stream", Path.GetFileName(resolved));
    });
    app.MapPost("/delivery-agent/v1/{id:guid}/confirm", (Guid id, DeliveryConfirmation input, HttpContext context) =>
    {
        var username = AgentUser(context);
        if (username is null) return Results.Unauthorized();
        return remoteDeliveries.Confirm(username, id, input.Sha256) ? Results.NoContent() : Results.NotFound();
    });
    app.MapPost("/delivery-agent/v1/{id:guid}/failure", (Guid id, DeliveryFailure input, HttpContext context) =>
    {
        var username = AgentUser(context);
        if (username is null) return Results.Unauthorized();
        return remoteDeliveries.RecordFailure(username, id, input.Code) ? Results.NoContent() : Results.NotFound();
    });
}
api.MapPost("/auth/login", (Login login, HttpContext c) =>
{
    if (string.IsNullOrWhiteSpace(login.Username) || login.Password is null || login.Username.Length > 80 || login.Password.Length > 1024) return Results.Unauthorized();
    var identity = database.Authenticate(login.Username, login.Password);
    if (identity is null) return Results.Unauthorized();
    foreach (var expired in sessions.Where(s => s.Value.Expiry <= DateTimeOffset.UtcNow)) sessions.TryRemove(expired.Key, out _);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); sessions[token] = (DateTimeOffset.UtcNow.AddHours(8), identity.Value.User, identity.Value.Role, identity.Value.MustChange);
    c.Response.Cookies.Append("sri-session", token, new CookieOptions { HttpOnly = true, SameSite = secureCookie ? SameSiteMode.None : SameSiteMode.Strict, Secure = secureCookie || c.Request.IsHttps, MaxAge = TimeSpan.FromHours(8), Path = "/" });
    c.Items["PanelUser"] = identity.Value.User;
    return Results.Ok(new { authenticated = true, user = identity.Value.User, role = identity.Value.Role, mustChange = identity.Value.MustChange });
}).RequireRateLimiting("login");
api.MapGet("/auth/session", (HttpContext c) => new { authenticated = true, user = c.Items["PanelUser"], role = c.Items["PanelRole"], mustChange = sessions[c.Request.Cookies["sri-session"]!].MustChange });
api.MapPost("/auth/change-password", (PasswordChange change, HttpContext c) => {
    var token=c.Request.Cookies["sri-session"]!; var old=sessions[token];
    database.ChangePassword(old.User,change.CurrentPassword,change.NewPassword);
    foreach(var item in sessions.Where(s=>s.Value.User==old.User)) sessions.TryRemove(item.Key,out _);
    sessions[token]=(old.Expiry,old.User,old.Role,false);
    return Results.Ok(new { changed=true });
}).RequireRateLimiting("login");
api.MapPost("/auth/logout", (HttpContext c) => { if (c.Request.Cookies["sri-session"] is { } token) sessions.TryRemove(token, out _); c.Response.Cookies.Delete("sri-session", new CookieOptions { Path = "/", SameSite = secureCookie ? SameSiteMode.None : SameSiteMode.Strict, Secure = secureCookie || c.Request.IsHttps }); return Results.NoContent(); });
api.MapGet("/users", (HttpContext c) => c.Items["PanelRole"] as string == "admin" ? Results.Ok(database.ListUsers()) : Results.StatusCode(403));
api.MapPost("/users", (PanelUserRequest request, HttpContext c) => {
    if (c.Items["PanelRole"] as string != "admin") return Results.StatusCode(403);
    database.SaveUser(request.Username, request.Role, request.Password);
    foreach (var session in sessions.Where(s => string.Equals(s.Value.User, request.Username.Trim(), StringComparison.OrdinalIgnoreCase))) sessions.TryRemove(session.Key, out _);
    return Results.Ok(new { saved = true });
});
api.MapGet("/assignments/{username}", (string username, HttpContext c) => c.Items["PanelRole"] as string == "admin" ? Results.Ok(database.AssignedAccounts(username)) : Results.StatusCode(403));
api.MapPost("/assignments", (RucAssignment assignment,HttpContext c) => { if(c.Items["PanelRole"] as string != "admin") return Results.StatusCode(403); database.Assign(assignment.Username,assignment.Account,assignment.Assigned); return Results.Ok(new {saved=true}); });
api.MapGet("/accounts", (IStateRepository r, HttpContext c) => r.Read(s => s.Accounts.Where(a => (!a.IsTest || TestView(c)) && Allowed(c,database,a.Id)).ToList()));
api.MapPost("/accounts", (Account a, IStateRepository r, HttpContext c) => r.Update(s => { a = a with { IsTest = a.IsTest || TestView(c) }; Validation.Identifier(a.Id); if (!string.IsNullOrEmpty(a.TaxId) && (a.TaxId.Length != 13 || !a.TaxId.All(char.IsAsciiDigit))) throw new ArgumentException("RUC inválido."); if (string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 150 || s.Accounts.Any(x => x.Id == a.Id)) throw new ArgumentException("Nombre inválido o cuenta existente."); s.Accounts.Add(a); return a; }));
api.MapGet("/dashboard", (IStateRepository r, IStorageProvider storage, HttpContext c) => r.Read(s => DashboardData.Build(s, storage, TestView(c), c.Items["PanelRole"] as string == "admin" ? null : database.AssignedAccounts((string)c.Items["PanelUser"]!))));
api.MapGet("/worker/status", (IStateRepository r) => r.Read(s => {
    var remote = string.Equals(Environment.GetEnvironmentVariable("SRI_REMOTE_EXECUTION_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
    var heartbeat = remote ? s.AgentHeartbeat : s.WorkerHeartbeat;
    return new { online = heartbeat > DateTimeOffset.UtcNow.AddSeconds(-15), heartbeat };
}));
api.MapGet("/agent/status", (IStateRepository r) => r.Read(s => new { online = s.AgentHeartbeat > DateTimeOffset.UtcNow.AddSeconds(-15), heartbeat = s.AgentHeartbeat }));
api.MapGet("/system", (IStorageProvider storage) => { storage.CheckAccess(); return new { outputPath = output, outputHostPath = Environment.GetEnvironmentVariable("SRI_OUTPUT_HOST_PATH"), timezone = "America/Guayaquil", sri = PortalReadiness.Status(Environment.GetEnvironmentVariable("SRI_PORTAL_PROFILE") ?? Path.GetFullPath("config/sri-portal.json")), types = Validation.Types }; });
api.MapGet("/executions", (IStateRepository r, HttpContext c) => r.Read(s => s.Executions.Where(e => DataScope.Visible(s, e.Query.Account, TestView(c)) && Allowed(c,database,e.Query.Account)).OrderByDescending(e => e.Created).ToList()));
api.MapGet("/executions/{id}", (string id, IStateRepository r) => r.Read(s => s.Executions.SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException()));
api.MapPost("/executions", (Query q, IStateRepository r,HttpContext c) => r.Update(s => { c.Items["AuditAccount"]=q.Account;var e=Validation.Enqueue(s,q);e.RequestedBy=(string)c.Items["PanelUser"]!;return e; }));
api.MapPost("/comprobantes", (Query q, IStateRepository r,HttpContext c) => r.Update(s => { c.Items["AuditAccount"]=q.Account;var e=Validation.Enqueue(s,q);e.RequestedBy=(string)c.Items["PanelUser"]!;return e; }));
api.MapPost("/executions/{id}/cancel", (string id, IStateRepository r) => r.Update(s => { var e = s.Executions.SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException(); e.CancelRequested = true; foreach(var op in s.Authentications.Where(a=>a.ExecutionId==id&&a.BrowserOpened)){op.CancelRequested=true;op.Command="Cancel";op.SessionVerified=false;op.Expires=null;} if (e.Status is "Pending" or "RequiresHumanAction") { e.Status = "Cancelled"; e.Stage = "Cancelled"; e.Message = "Cancelada por el usuario desde Angular."; e.Finished = DateTimeOffset.UtcNow; } return e; }));
api.MapPost("/executions/{id}/resume", (string id, IStateRepository r) => r.Update(s =>
{
    var e = s.Executions.SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException();
    if (e.Status != "RequiresHumanAction") throw new ArgumentException("No está suspendida.");
    if (s.Executions.Any(x => x.Id != id && x.Query.Account == e.Query.Account && x.Status is "Pending" or "Running")) throw new ArgumentException("La cuenta ya tiene un trabajo activo.");
    e.Status = "Pending"; e.Stage = "Ready"; e.CancelRequested = false; e.Finished = null; e.Attempts = 0;
    e.Message = "Reanudación solicitada desde el último checkpoint confirmado."; return e;
}));
api.MapPost("/executions/{id}/retry", (string id, IStateRepository r, HttpContext c) => r.Update(s =>
{
    var old = s.Executions.SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException();
    if (old.Status is not "Failed" and not "Cancelled" and not "CompletedWithWarnings") throw new ArgumentException("El trabajo no admite reintento.");
    var next = Validation.Enqueue(s, old.Query); next.ParentExecutionId = old.Id; next.RequestedBy = (string)c.Items["PanelUser"]!; return next;
}));
app.MapAccountEndpoints(api);
app.MapPanelEndpoints(api, database);
api.MapGet("/configurations", (IStateRepository r, HttpContext c) => r.Read(s => s.Configurations.Where(q => DataScope.Visible(s, q.Account, TestView(c)) && Allowed(c,database,q.Account)).ToList()));
api.MapPost("/configurations", (Query q, IStateRepository r) => r.Update(s => { Validation.Query(q, s); s.Configurations.Add(q); return q; }));
api.MapGet("/documents", (IStateRepository r, HttpContext c, string? account, string? type, string? search, string? status, string? source,DateOnly? from, DateOnly? to, int page = 1, int pageSize = 20) => r.Read(s =>
{
    var items = s.Documents.Where(d => DataScope.Visible(s, d.Account, TestView(c)) && Allowed(c,database,d.Account) && (account is null || d.Account == account) && (type is null || d.Type == type) && (status is null || d.Status == status) && (!from.HasValue || d.Issued >= from) && (!to.HasValue || d.Issued <= to) && (string.IsNullOrWhiteSpace(search) || (d.Recipient ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) || (d.Number ?? "").Contains(search)));
    if(source is not null)items=items.Where(d=>d.Source==source);
    return new { total = items.Count(), items = items.OrderByDescending(d => d.Issued).Skip((Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 100)).Take(Math.Clamp(pageSize, 1, 100)).ToList() };
}));
api.MapGet("/sharepoint/uploads", (IStateRepository r, HttpContext c) =>
{
    if (c.Items["PanelRole"] as string != "admin") return Results.StatusCode(403);
    var items = r.Read(s => s.SharePointUploads.OrderByDescending(u => u.Created).Take(200).Select(u => new { u.Id, u.DocumentId, u.Account, u.TaxId, u.User, u.Extension, u.RemotePath, u.Status, u.Attempts, u.Created, u.Completed, u.NextAttempt, u.Error, u.Cleaned, u.CleanupError }).ToList());
    return Results.Ok(items);
});
api.MapPost("/sharepoint/uploads/{id}/retry", (string id, IStateRepository r, HttpContext c) =>
{
    if (c.Items["PanelRole"] as string != "admin") return Results.StatusCode(403);
    r.Update(s =>
    {
        var item = s.SharePointUploads.SingleOrDefault(u => u.Id == id) ?? throw new KeyNotFoundException();
        if (item.Status != "Failed") throw new ArgumentException("Solo se pueden reintentar publicaciones fallidas.");
        item.Status = "Pending"; item.NextAttempt = null; item.Error = null;
        AuditTrail.Add(s, (string)c.Items["PanelUser"]!, "Publicación SharePoint", item.Account, $"Reintento solicitado: {item.RemotePath}");
        return true;
    });
    return Results.Ok(new { queued = true });
});
api.MapPost("/documents/import/{account}", async (string account, HttpRequest request, IStateRepository r, IDocumentParser parser, IStorageProvider storage) =>
{
    var registeredAccount = r.Read(s => s.Accounts.SingleOrDefault(a => a.Id == account)) ?? throw new ArgumentException("Cuenta inexistente.");
    using var content = new MemoryStream(); await request.Body.CopyToAsync(content); var bytes = content.ToArray(); var doc = parser.ParseXml(account, bytes); Validation.DocumentAccount(registeredAccount, doc); doc.XmlPath = storage.Save(doc, "xml", bytes); doc.XmlSha256 = DownloadPipeline.Hash(bytes);
    return r.Update(s => { var existing = s.Documents.SingleOrDefault(d => d.Account == account && d.Type == doc.Type && d.AccessKey == doc.AccessKey); if (existing is not null) return existing; s.Documents.Add(doc); return doc; });
});
api.MapPost("/documents/reindex/{account}", (string account, IStateRepository r, IStorageProvider storage, IDocumentParser parser) => new DocumentIndexer(r, storage, parser).Rebuild(account));
api.MapPost("/documents/{id}/pdf", async (string id, HttpRequest request, IStateRepository r, IStorageProvider storage) =>
{
    var doc = r.Read(s => s.Documents.SingleOrDefault(d => d.Id == id) ?? throw new KeyNotFoundException());
    using var content = new MemoryStream(); await request.Body.CopyToAsync(content); var bytes = content.ToArray(); var path = storage.Save(doc, "pdf", bytes); var warning = new PdfInspector().Check(bytes, doc.AccessKey);
    return r.Update(s => { var d = s.Documents.Single(d => d.Id == id); d.PdfPath = path; d.PdfSha256 = DownloadPipeline.Hash(bytes); if (warning is not null) { d.Status = "Revisar"; d.Warning = warning; } return d; });
});
api.MapGet("/documents/{id}/{extension}", async (string id, string extension, IStateRepository r, IStorageProvider storage, HttpContext c, CancellationToken cancellation) =>
{
    var doc = r.Read(s => s.Documents.SingleOrDefault(d => d.Id == id) ?? throw new KeyNotFoundException());
    if (!Allowed(c, database, doc.Account)) return Results.StatusCode(403);
    var path = extension switch { "xml" => doc.XmlPath, "pdf" => doc.PdfPath, _ => throw new ArgumentException("Extensión inválida.") } ?? throw new KeyNotFoundException("Archivo no disponible.");
    var remote = r.Read(s => s.SharePointUploads.FirstOrDefault(u => u.DocumentId == id && u.Extension == extension && u.Status == "Completed"));
    if (remote is not null)
    {
        var graph = c.RequestServices.GetService<SharePointGraphClient>() ?? throw new IOException("SharePoint no está configurado.");
        var bytes = await graph.ReadIfExists(remote.RemotePath, cancellation) ?? throw new KeyNotFoundException("Archivo remoto no disponible.");
        if (DownloadPipeline.Hash(bytes) != remote.Sha256) throw new InvalidDataException("El archivo remoto no supera la comprobación de integridad.");
        return Results.File(bytes, extension == "xml" ? "application/xml" : "application/pdf", Path.GetFileName(path));
    }
    return Results.File(storage.Resolve(path), extension == "xml" ? "application/xml" : "application/pdf", Path.GetFileName(path));
});
api.MapPost("/reports", (ReportRequest request, IStateRepository r, IStorageProvider storage,HttpContext c) =>
{
    c.Items["AuditAccount"]=request.Account;
    var from = new DateOnly(request.Year, request.Month, 1); var to = request.To ?? from; if (to < from || to.Year - from.Year > 20) throw new ArgumentException("Rango inválido.");
    if (!r.Read(s => s.Accounts.Any(a => a.Id == request.Account))) throw new ArgumentException("Cuenta inexistente.");
    var documents = r.Read(s => s.Documents.Where(d => d.Account == request.Account).ToList()); var paths = new List<string>();
    foreach (var period in documents.Where(d => d.Issued >= from && d.Issued <= to.AddMonths(1).AddDays(-1)).Select(d => (d.Issued.Year, d.Issued.Month)).Distinct().OrderBy(x => x.Year).ThenBy(x => x.Month))
    {
        var path = storage.Report(request.Account, period.Year, period.Month, documents);
        paths.Add(path);
        if (postgresConnection is not null)
        {
            var account = r.Read(s => s.Accounts.Single(a => a.Id == request.Account));
            var taxId = account.TaxId ?? account.Id;
            if (taxId.Length == 13 && taxId.All(char.IsAsciiDigit))
            {
                var bytes = File.ReadAllBytes(storage.Resolve(path));
                var hash = DownloadPipeline.Hash(bytes);
                var snapshot = Path.Combine(".deliveries", hash + ".xlsx");
                var snapshotPath = storage.Resolve(snapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                var temporary = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
                    try { File.Move(temporary, snapshotPath, false); }
                    catch (IOException) when (File.Exists(snapshotPath) && DownloadPipeline.Hash(File.ReadAllBytes(snapshotPath)) == hash) { }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                var name = $"Resumen_{period.Year:D4}_{period.Month:D2}.xlsx";
                new RemoteDeliveryStore(postgresConnection).Enqueue((string)c.Items["PanelUser"]!, request.Account,
                    $"report-{request.Account}-{period.Year:D4}-{period.Month:D2}", "xlsx", snapshot,
                    $"{taxId}/{period.Year:D4}/{period.Month:D2}/Reportes/{name}", hash);
            }
        }
    }
    return new { paths };
});
api.MapGet("/reports/{account}/{year:int}/{month:int}", MonthlyReportEndpoint.Download);
api.MapGet("/schedules", (IStateRepository r, HttpContext c) => r.Read(s => s.Schedules.Where(x => DataScope.Visible(s, x.Query.Account, TestView(c)) && Allowed(c,database,x.Query.Account)).ToList()));
api.MapPost("/schedules", (Schedule x, IStateRepository r,HttpContext c) => r.Update(s => { c.Items["AuditAccount"]=x.Query.Account;ValidateSchedule(x, s); x.Id = Guid.NewGuid().ToString("N");x.CreatedBy=(string)c.Items["PanelUser"]!; s.Schedules.Add(x); return x; }));
api.MapPut("/schedules/{id}", (string id, Schedule x, IStateRepository r,HttpContext c) => r.Update(s => { c.Items["AuditAccount"]=x.Query.Account;ValidateSchedule(x, s); var index = s.Schedules.FindIndex(x => x.Id == id); if (index < 0) throw new KeyNotFoundException(); x.Id = id;x.CreatedBy=s.Schedules[index].CreatedBy; s.Schedules[index] = x; return x; }));
api.MapDelete("/schedules/{id}", (string id, IStateRepository r) => r.Update(s => s.Schedules.RemoveAll(x => x.Id == id)));
api.MapPost("/schedules/{id}/run", (string id, IStateRepository r,HttpContext c) => r.Update(s => {var e=Validation.Enqueue(s, s.Schedules.SingleOrDefault(x => x.Id == id)?.Query ?? throw new KeyNotFoundException());e.RequestedBy=(string)c.Items["PanelUser"]!;return e;}));
app.Run();
static void ValidateSchedule(Schedule x, State s) { Validation.Query(x.Query, s); _ = Validation.Next(x.Next, x.Frequency,x.IntervalDays); if (x.Enabled&&x.Next <= DateTimeOffset.UtcNow) throw new ArgumentException("La fecha debe ser futura."); }



static bool Allowed(HttpContext c, IPanelDatabase db, string account) => c.Items["PanelRole"] as string == "admin" || db.AssignedAccounts((string)c.Items["PanelUser"]!).Contains(account);
static bool TestView(HttpContext c) => c.Request.Headers["X-SRI-Test-Data"] == "1";
record Login(string Username, string Password);
record PanelUserRequest(string Username, string Role, string Password);
record PasswordChange(string CurrentPassword, string NewPassword);
record RucAssignment(string Username, string Account, bool Assigned);
record ReportRequest(string Account, int Year, int Month, DateOnly? To);
record AgentRegistration(string Label);
record DeliveryConfirmation(string Sha256);
record DeliveryFailure(string Code);
