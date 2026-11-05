using SriAutomation.Domain;
namespace SriAutomation.Application;
public static class ExecutionProgress
{
    public static void Set(State state,string id,string stage,string message)
    {
        var job=state.Executions.Single(e=>e.Id==id);job.Stage=stage;job.Message=message;
        if(job.Events.LastOrDefault() is {} previous&&previous.Stage==stage&&previous.Message==message)return;
        job.Events.Add(new(DateTimeOffset.UtcNow,stage,message));
        if(job.Events.Count>100)job.Events.RemoveAt(0);
    }
}
