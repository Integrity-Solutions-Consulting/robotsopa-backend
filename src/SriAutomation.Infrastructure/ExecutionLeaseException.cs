namespace SriAutomation.Infrastructure;

public sealed class ExecutionLeaseException() : InvalidOperationException("Execution ownership has expired or changed.");
