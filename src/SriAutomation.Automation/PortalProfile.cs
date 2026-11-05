using System.Text.Json;
using SriAutomation.Application;

namespace SriAutomation.Automation;

// Ninguno de estos selectores tiene valores de producción inferidos.
public sealed class PortalProfile
{
    public bool RuntimeObserved { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public BoundSriSession? BoundSession { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<string>? TraceStep { get; set; }
    public string Url { get; set; } = SriPortal.PortalUrl;
    public bool Verified { get; set; }
    public string? VerifiedAt { get; set; }
    public string Evidence { get; set; } = "";
    public string AccountIdentity { get; set; } = "";
    public string Authenticated { get; set; } = "";
    public string Login { get; set; } = "";
    public string HumanChallenge { get; set; } = "";
    public string[] Navigation { get; set; } = [];
    public string Date { get; set; } = "";
    public string DateFormat { get; set; } = "dd/MM/yyyy";
    public string? Year { get; set; }
    public string? Month { get; set; }
    public string? Day { get; set; }
    public string MonthFormat { get; set; } = "M";
    public string DayFormat { get; set; } = "d";
    public string Type { get; set; } = "";
    public Dictionary<string, string> TypeValues { get; set; } = [];
    public string? Establishment { get; set; }
    public string? EmissionPoint { get; set; }
    public string? Status { get; set; }
    public Dictionary<string, string> StatusValues { get; set; } = [];
    public string Search { get; set; } = "";
    public string Ready { get; set; } = "";
    public string? Busy { get; set; }
    // Respuesta que confirma el AJAX de búsqueda/paginación; regex de URL observado.
    public string ResponseUrlPattern { get; set; } = "";
    public string[] AjaxFilters { get; set; } = [];
    public string Rows { get; set; } = "";
    public string Empty { get; set; } = "";
    public string Next { get; set; } = "";
    public string NextDisabled { get; set; } = "";
    public string Key { get; set; } = "";
    public string Issued { get; set; } = "";
    public string IssuedFormat { get; set; } = "dd/MM/yyyy";
    public string Xml { get; set; } = "";
    public string Pdf { get; set; } = "";
    public string Logout { get; set; } = "";
    public int TimeoutMs { get; set; } = 30000;
    public int MaxPages { get; set; } = 500;
    public int MaxDocuments { get; set; } = 10;
    public int MinQueryIntervalMs { get; set; } = 3000;
    public int MaxAutomaticDays { get; set; } = 1;
    public void Validate(bool fixture = false)
    {
        if (!Verified || string.IsNullOrWhiteSpace(VerifiedAt) || string.IsNullOrWhiteSpace(Evidence)) throw new HumanActionRequiredException("Falta un perfil de selectores inspeccionado y validado para esta versión del portal.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (!fixture && (uri.Scheme != "https" || uri.Host != "srienlinea.sri.gob.ec"))) throw new ArgumentException("Destino de automatización no autorizado.");
        if ((!RuntimeObserved&&new[] { Rows, Empty, Next, NextDisabled, Key, Issued }.Any(string.IsNullOrWhiteSpace))||new[] { AccountIdentity, Authenticated, Login, Type, Search, Ready, ResponseUrlPattern }.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(Date) && new[] { Year, Month, Day }.Any(string.IsNullOrWhiteSpace))
            throw new HumanActionRequiredException("El perfil inspeccionado está incompleto.");
        if (TimeoutMs is < 1000 or > 120000 || MaxPages is < 1 or > 10000 || MaxDocuments is < 1 or > 100000 || MinQueryIntervalMs < 0 || MaxAutomaticDays < 1) throw new ArgumentException("Límites de automatización inválidos.");
    }
    public static PortalProfile Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<PortalProfile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Perfil vacío.")
        : throw new HumanActionRequiredException("No existe un perfil de selectores verificados. Complete la inspección local autorizada.");
}
