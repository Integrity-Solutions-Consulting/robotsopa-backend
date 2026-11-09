#if SOPA_SYNTHETIC_FIXTURE
using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.LocalAgent;

// Compiled only into an explicitly isolated test binary; never contacts SRI.
internal static class SyntheticFixture
{
    private sealed record Fixture(string Account, string Key, DateOnly Issued, byte[] Pdf, byte[] Xml);
    public static Task Execute(RemoteOwnedExecution remote, Execution job, string root, CancellationToken cancellation)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllBytes(Path.Combine(root, "fixture.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new IOException("Datos sintéticos inválidos.");
        if (job.Query.Account != fixture.Account || fixture.Issued < job.Query.From || fixture.Issued > job.Query.To || !job.Query.Types.Contains("Facturas")) throw new IOException("El trabajo no coincide con el fixture sintético.");
        cancellation.ThrowIfCancellationRequested();
        remote.Update(s => { var e = s.Executions.Single(); if (!e.AttemptedKeys.Contains(fixture.Key)) e.AttemptedKeys.Add(fixture.Key); return true; });
        var existing = remote.Read(s => s.Documents.SingleOrDefault(d => d.Account == fixture.Account && d.AccessKey == fixture.Key));
        var pdf = job.Query.Pdf && (existing is null || !remote.IsAvailable(existing, "pdf")) ? remote.Pending(fixture.Account, "Facturas", fixture.Key, fixture.Issued, "pdf") ?? fixture.Pdf : null;
        var xml = job.Query.Xml && (existing is null || !remote.IsAvailable(existing, "xml")) ? remote.Pending(fixture.Account, "Facturas", fixture.Key, fixture.Issued, "xml") ?? fixture.Xml : null;
        if (pdf is not null || xml is not null) remote.Process(new(fixture.Account, "Facturas", fixture.Key, fixture.Issued, xml, pdf));
        remote.Update(s => { var e = s.Executions.Single(); e.SriCheckpoint = new(fixture.Issued, "Facturas", 0, 1, true); e.Queries.RemoveAll(q => q.Date == fixture.Issued && q.Type == "Facturas"); e.Queries.Add(new(fixture.Issued, "Facturas", 1, 1, 0, "Consultado")); return true; });
        Console.WriteLine("Fixture sintético procesado; sin conexión al SRI.");
        return Task.CompletedTask;
    }
}
#endif
