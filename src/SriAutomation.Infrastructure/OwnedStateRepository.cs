using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>
/// Execution-scoped state access. Every write is fenced by PostgreSQL in the
/// same transaction as the state mutation. This wrapper does not fence
/// filesystem or network operations and must not be treated as doing so.
/// </summary>
public sealed class OwnedStateRepository(PostgresStateRepository repository, ExecutionOwnership ownership) : IStateRepository
{
    public ExecutionOwnership Ownership => ownership;
    public T Read<T>(Func<State, T> read) => repository.Read(read);

    public T Update<T>(Func<State, T> mutate) => repository.UpdateOwned(ownership, mutate);

    /// <summary>Checks ownership transactionally before starting an external operation.</summary>
    public void VerifyOwnership() => repository.UpdateOwned(ownership, _ => true);
}
