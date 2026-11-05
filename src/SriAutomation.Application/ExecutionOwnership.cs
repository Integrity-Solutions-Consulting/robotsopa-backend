namespace SriAutomation.Application;

/// <summary>Identifies one generation of ownership of a job.</summary>
public sealed record ExecutionOwnership(string JobId, string WorkerId, Guid LeaseId, long FencingToken);
