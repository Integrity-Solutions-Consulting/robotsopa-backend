using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>
/// Transactional PostgreSQL implementation of the legacy state contract.
/// </summary>
public sealed class PostgresStateRepository(string connectionString) : IStateRepository
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public T Read<T>(Func<State, T> read)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id = 1", connection);
        return read(Deserialize(command.ExecuteScalar()));
    }

    public T Update<T>(Func<State, T> mutate)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var select = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id = 1 FOR UPDATE", connection, transaction);
        var state = Deserialize(select.ExecuteScalar());
        var result = mutate(state);
        PostgresPanelDatabase.SyncAccounts(connection, transaction, state.Accounts);
        PostgresPanelDatabase.SyncAudit(connection, transaction, state.Audit);
        using var update = new NpgsqlCommand("UPDATE app_state SET payload = @payload::jsonb WHERE id = 1", connection, transaction);
        update.Parameters.AddWithValue("payload", NpgsqlDbType.Text, JsonSerializer.Serialize(state, Options));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("PostgreSQL state row is missing.");
        transaction.Commit();
        return result;
    }

    private static State Deserialize(object? value)
    {
        if (value is not string json) throw new InvalidOperationException("PostgreSQL state row is missing; initialize the database first.");
        return JsonSerializer.Deserialize<State>(json, Options) ?? throw new InvalidDataException("PostgreSQL state is invalid.");
    }
}
