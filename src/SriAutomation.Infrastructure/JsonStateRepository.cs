using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class JsonStateRepository : IStateRepository
{
    private readonly string directory;
    private readonly PanelDatabase database;
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public JsonStateRepository(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        database = new PanelDatabase(this.directory);
        foreach (var name in new[] { "configurations", "executions", "manifests", "checkpoints", "logs" }) Directory.CreateDirectory(Path.Combine(this.directory, name));
    }
    private FileStream Lock()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try { return new FileStream(Path.Combine(directory, ".state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(30); }
        }
    }
    private State Load()
    {
        var path = Path.Combine(directory, "state.json");
        if (!File.Exists(path)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(path), options) ?? throw new JsonException(); }
        catch (JsonException)
        {
            // Una copia corrupta nunca se sustituye silenciosamente por un estado vacío.
            var backup = path + ".bak";
            if (!File.Exists(backup)) throw new InvalidDataException("Estado corrupto sin respaldo válido.");
            return JsonSerializer.Deserialize<State>(File.ReadAllText(backup), options) ?? throw new InvalidDataException("Respaldo inválido.");
        }
    }
    public T Read<T>(Func<State, T> read) { using var gate = Lock(); return read(Load()); }
    public T Update<T>(Func<State, T> mutate)
    {
        using var gate = Lock();
        var state = Load();
        var auditCount = state.Audit.Count;
        var accountsBefore = System.Text.Json.JsonSerializer.Serialize(state.Accounts);
        var result = mutate(state);
        var path = Path.Combine(directory, "state.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, state, options);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (accountsBefore != System.Text.Json.JsonSerializer.Serialize(state.Accounts)) database.SyncAccounts(state.Accounts);
        if (state.Audit.Count > auditCount) database.SyncAudit(state.Audit.Skip(auditCount));
        return result;
    }
}
