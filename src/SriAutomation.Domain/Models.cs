namespace SriAutomation.Domain;

public sealed record Account(string Id, string Name, string? TaxId = null, bool IsTest = false);
public sealed record Query(string Account, DateOnly From, DateOnly To, string[] Types,
    string Mode = "Sri", bool Pdf = true, bool Xml = true, string? Establishment = null, string? EmissionPoint = null,
    string AuthorizationStatus = "Autorizado", bool BroadRangeApproved = false, int MaxDocuments = 10, string StorageMode = "LEGACY");
public sealed class Execution
{
    public List<ExecutionEvent> Events { get; set; } = [];
    public List<ExecutionReport> Reports { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentExecutionId { get; set; }
    public Query Query { get; set; } = null!;
    public string Status { get; set; } = "Pending";
    public string Stage { get; set; } = "Ready";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public bool CancelRequested { get; set; }
    public int Processed { get; set; }
    public int Found { get; set; }
    public int Errors { get; set; }
    public int Reviews { get; set; }
    public int Attempts { get; set; }
    public string? Checkpoint { get; set; }
    public string? Message { get; set; }
    public SriCheckpoint? SriCheckpoint { get; set; }
    public List<QueryResult> Queries { get; set; } = [];
    public int Downloaded { get; set; }
    public List<string> Issues { get; set; } = [];
    public List<string> AttemptedKeys { get; set; } = [];
    public string? RequestedBy { get; set; }
    // Set by the server on each claim, never accepted from a state update.
    public bool CentralStorageAgent { get; set; }
    public int DeliveryTotal { get; set; }
    public int DeliveryCompleted { get; set; }
    public string? DeliveryError { get; set; }
}
public sealed record ExecutionEvent(DateTimeOffset At,string Stage,string Message);
public sealed record ExecutionReport(int Year,int Month,string Path);
public sealed record PublishedMonthlyReport(string Account, int Year, int Month, string Path, string Sha256, DateTimeOffset PublishedAt);
public sealed class Voucher
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Account { get; set; } = "";
    public string Type { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string? IssuerTaxId { get; set; }
    public string? IssuerName { get; set; }
    public string? Number { get; set; }
    public string? Authorization { get; set; }
    public string? AuthorizedAt { get; set; }
    public string? Recipient { get; set; }
    public string? Identification { get; set; }
    public DateOnly Issued { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? Discount { get; set; }
    public decimal? Taxes { get; set; }
    public decimal? Total { get; set; }
    public string? PdfPath { get; set; }
    public string? XmlPath { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? Warning { get; set; }
    public Dictionary<string, FieldEvidence> FieldEvidence { get; set; } = new();
    public string Source { get; set; } = "Imported";
    public string? XmlSha256 { get; set; }
    public string? PdfSha256 { get; set; }
    public long PdfGeneration { get; set; } = 1;
    public long XmlGeneration { get; set; } = 1;
    public List<PdfVersion> PdfHistory { get; set; } = [];
}
public sealed record FieldEvidence(string Source, string Confidence);
public sealed record PdfVersion(string Path, string Sha256, DateTimeOffset ReplacedAt);
public sealed record SriCheckpoint(DateOnly Date, string Type, int Page, int Row, bool Complete = false);
public sealed record QueryResult(DateOnly Date, string Type, int Found, int Downloaded, int Errors, string Status);
public sealed class LocalDelivery
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExecutionId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string Extension { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? Completed { get; set; }
}
public sealed class SharePointUpload
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; set; } = "";
    public string Account { get; set; } = "";
    public string TaxId { get; set; } = "";
    public string User { get; set; } = "";
    public string Extension { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextAttempt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LeaseId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? Completed { get; set; }
    public bool Cleaned { get; set; }
    public string? CleanupError { get; set; }
}
public sealed class Schedule
{
    public int IntervalDays { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Query Query { get; set; } = null!;
    public string Frequency { get; set; } = "Daily";
    public DateTimeOffset Next { get; set; }
    public bool Enabled { get; set; } = true;
    public string? CreatedBy { get; set; }
}
public sealed class State
{
    // Preserve fields written by newer or external components when state is rewritten.
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; set; }
    public string AuthenticationMode { get; set; } = "";
    public List<AuditEvent> Audit { get; set; } = [];
    public PanelSettings Settings { get; set; } = new();
    public List<AuthenticationOperation> Authentications { get; set; } = [];
    public DateTimeOffset? AgentHeartbeat { get; set; }
    public List<PortalControl> PortalControls { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
    public List<Query> Configurations { get; set; } = [];
    public List<Execution> Executions { get; set; } = [];
    public List<PublishedMonthlyReport> PublishedReports { get; set; } = [];
    public List<Voucher> Documents { get; set; } = [];
    public List<SharePointUpload> SharePointUploads { get; set; } = [];
    public List<LocalDelivery> LocalDeliveries { get; set; } = [];
    public List<Schedule> Schedules { get; set; } = [];
    public DateTimeOffset? WorkerHeartbeat { get; set; }
}
public sealed class PanelSettings
{
    public string? DefaultAccount { get; set; }
    public string? DownloadSubfolder { get; set; }
}
public sealed record AuditEvent(string Id, string User, string Action, DateTimeOffset At, string? Account, string Result, bool IsTest = false,string? AccountName=null);

public sealed class AuthenticationOperation
{
    public List<BrowserObservation> Observations { get; set; } = [];
    public string? VerificationCode { get; set; }
    public string? ExecutionId { get; set; }
    public string Owner { get; set; } = "Agent";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Account { get; set; } = "";
    public string Status { get; set; } = "Ready";
    public string Command { get; set; } = "Open";
    public bool CancelRequested { get; set; }
    public bool BrowserOpened { get; set; }
    public bool SessionVerified { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Expires { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string Message { get; set; } = "Solicitud pendiente del agente local.";
}
public sealed record PortalControl(string Tag, string? Id, string? Type);
public sealed record BrowserObservation(DateTimeOffset At,string Phase,string Location,int Tabs,int Frames,bool Closed);
