using Microsoft.Data.SqlClient;

namespace DaprCompat.App;

/// <summary>Identity of this replica, as configured by Compose.</summary>
public static class Replica
{
    public static string Name { get; } = Environment.GetEnvironmentVariable("REPLICA_NAME") ?? Environment.MachineName;
}

/// <summary>
/// Writes observations to SpikeDb.dbo.Evidence. Every side effect the experiments count
/// (activity executions, actor calls, deliveries, invocations) is recorded here, so results
/// come from persisted state, not from logs.
/// </summary>
public sealed class Evidence(string connectionString)
{
    public async Task AddAsync(string kind, string key, string detail = "", CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "INSERT dbo.Evidence (Kind, [Key], Host, Detail) VALUES (@kind, @key, @host, @detail)", connection);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@host", Replica.Name);
        command.Parameters.AddWithValue("@detail", detail);
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed record FlakyRequest(string Key, int Fail);
