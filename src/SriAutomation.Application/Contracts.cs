using SriAutomation.Domain;

namespace SriAutomation.Application;

public interface IStateRepository
{
    T Read<T>(Func<State, T> read);
    T Update<T>(Func<State, T> mutate);
}
public interface IDocumentParser { Voucher ParseXml(string account, byte[] xml); }
public interface IStorageProvider
{
    string AccountDirectory(string account) => Validation.Identifier(account);
    void CheckAccess();
    string Save(Voucher document, string extension, byte[] content);
    string Resolve(string relative);
    string Report(string account, int year, int month, IEnumerable<Voucher> documents);
}
public interface ISriComprobanteService
{
    Task Execute(Query query, CancellationToken cancellation);
    Task Execute(Execution execution, CancellationToken cancellation) => Execute(execution.Query, cancellation);
}
public sealed class HumanActionRequiredException(string message) : Exception(message);
public sealed class SriUnavailableException(string message) : IOException(message);

public static class Validation
{
    public static readonly string[] Types = ["Facturas", "Retenciones", "Notas-Credito", "Notas-Debito", "Liquidaciones", "Guias-Remision"];
    public static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Identificador inválido.");
        return value;
    }
    public static void Query(Query query, State state)
    {
        if (query is null) throw new ArgumentException("Configuración requerida.");
        if (!state.Accounts.Any(a => a.Id == query.Account)) throw new ArgumentException("Seleccione una cuenta registrada.");
        if (query.To < query.From || query.To > DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "America/Guayaquil").Date))
            throw new ArgumentException("Rango de fechas inválido.");
        if (query.Types is null || query.Types.Length == 0 || query.Types.Any(t => !Types.Contains(t))) throw new ArgumentException("Seleccione tipos válidos.");
        if(query.Types.Distinct().Count()!=query.Types.Length)throw new ArgumentException("No repita tipos de comprobante.");
        if(query.To > query.From.AddYears(1).AddDays(-1))throw new ArgumentException("Cada operación admite hasta un año; seleccione un rango menor.");
        if (query.Mode is not "Sri" and not "Stored") throw new ArgumentException("Modo inválido.");
        if (!query.Pdf && !query.Xml) throw new ArgumentException("Seleccione PDF o XML.");
        foreach (var code in new[] { query.Establishment, query.EmissionPoint })
            if (!string.IsNullOrEmpty(code) && (code.Length != 3 || !code.All(char.IsAsciiDigit))) throw new ArgumentException("Establecimiento y punto deben tener tres dígitos.");
    }
    public static Execution Enqueue(State state, Query query)
    {
        Query(query, state);
        if (state.Executions.Any(e => e.Query.Account == query.Account && e.Status is "Pending" or "Running"))
            throw new ArgumentException("La cuenta ya tiene una ejecución pendiente o activa.");
        var execution = new Execution { Query = query.Mode == "Sri" ? query with { Pdf = true, Xml = false } : query };
        state.Executions.Add(execution);
        return execution;
    }
    public static void DocumentAccount(Account account, Voucher document)
    {
        var ruc = !string.IsNullOrWhiteSpace(account.TaxId) ? account.TaxId : account.Id.Length == 13 && account.Id.All(char.IsAsciiDigit) ? account.Id : null;
        if (ruc is not null && document.IssuerTaxId != ruc) throw new ArgumentException("El emisor del XML no corresponde a la cuenta seleccionada.");
    }
    public static DateTimeOffset Next(DateTimeOffset current, string frequency,int intervalDays=1)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil");
        var local = TimeZoneInfo.ConvertTime(current, zone);
        return frequency switch
        {
            "Daily" => local.AddDays(1),
            "Weekly" => local.AddDays(7),
            "Monthly" => local.AddMonths(1),
            "Custom" when intervalDays is >=1 and <=365 => local.AddDays(intervalDays),
            "Once" => DateTimeOffset.MaxValue,
            _ => throw new ArgumentException("Frecuencia inválida.")
        };
    }
}
