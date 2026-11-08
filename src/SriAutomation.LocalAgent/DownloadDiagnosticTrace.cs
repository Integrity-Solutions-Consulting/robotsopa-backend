using System.Text.Json;

namespace SriAutomation.LocalAgent;

// The caller supplies only fixed event names and sanitized structural categories.
internal sealed class DownloadDiagnosticTrace
{
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation", "diagnostico", "query-steps.jsonl");
    public void Start()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("No se pudo crear el diagnóstico local de descargas."); }
    }
    public void Write(string step)
    {
        try { File.AppendAllText(FilePath, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, step }) + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("No se pudo escribir el diagnóstico local de descargas."); }
    }
}
