using System.Text.Json;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

public class StateCompatibilityTests
{
    [Fact]
    public void StateRoundTripPreservesUnknownTopLevelFields()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = JsonSerializer.Deserialize<State>("{\"external\":{\"token\":7},\"accounts\":[]}", options)!;
        state.WorkerHeartbeat = DateTimeOffset.UtcNow;
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(state, options));
        Assert.Equal(7, output.RootElement.GetProperty("external").GetProperty("token").GetInt32());
    }
}
