using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public sealed class RemoteProgressTests
{
    [Theory]
    [InlineData("fila: No se encontró un control de descarga disponible.", "No se encontró un control de descarga disponible.")]
    [InlineData("detalle privado 1234567890", "El Bot no confirmó la descarga o validación de un comprobante.")]
    public void RemoteErrorsCreateSafeVisibleEventForOldAndNewBots(string issue, string expected)
    {
        var query = new Query("fixture", new(2026, 9, 12), new(2026, 9, 12), ["Facturas"]);
        var state = new State { Executions = [new Execution { Id = "job", Status = "Running", Query = query }] };
        var delta = new State { Executions = [new Execution { Id = "job", Status = "Running", Query = query, Errors = 1, Stage = "Processing", Issues = [issue] }] };
        NativeExecution.ApplyRemote(state, "job", delta);
        Assert.Contains(state.Executions[0].Events, e => e.Stage == "DocumentError" && e.Message == expected);
        NativeExecution.ApplyRemote(state, "job", delta);
        Assert.Single(state.Executions[0].Events, e => e.Stage == "DocumentError");
    }
}
