using System.Text.Json;
namespace SriAutomation.Infrastructure;

public static class PortalReadiness
{
    public static string Status(string path)
    {
        if (!File.Exists(path)) return "ProfileMissing";
        try { using var profile = JsonDocument.Parse(File.ReadAllText(path)); return profile.RootElement.TryGetProperty("verified", out var value) && value.ValueKind == JsonValueKind.True ? "SessionValidationRequired" : "ProfilePending"; }
        catch (JsonException) { return "ProfileInvalid"; }
    }
}
