using Fulfillment.Contracts.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Diagnostics;

/// <summary>
/// SQL side of the runtime probe in OrderDb (schema diagnostics). One row per probe id is the
/// unique start record; the claim is the execution fence: a probe that is already Completed
/// never runs its effects again, whatever Dapr does with the instance id. The same pattern
/// guards fulfillment orders (the start request's execution claim). The diagnostics schema is
/// owned by the probe and created idempotently at startup; it is deliberately not part of the
/// business schema, which comes from EF Core migrations.
/// </summary>
public sealed class ProbeStore(string connectionString)
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await ExecuteAsync(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            -- Replicas starting together on a fresh database must not race between the check and the creation.
            EXEC sp_getapplock @Resource = 'diagnostics.schema', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
            IF SCHEMA_ID('diagnostics') IS NULL EXEC('CREATE SCHEMA diagnostics');
            IF OBJECT_ID('diagnostics.RuntimeProbe') IS NULL
                CREATE TABLE diagnostics.RuntimeProbe (
                    ProbeId         nvarchar(64)  NOT NULL CONSTRAINT PK_RuntimeProbe PRIMARY KEY,
                    Status          nvarchar(20)  NOT NULL,
                    Claims          int           NOT NULL CONSTRAINT DF_RuntimeProbe_Claims DEFAULT 0,
                    FenceRejections int           NOT NULL CONSTRAINT DF_RuntimeProbe_Fence DEFAULT 0,
                    CreatedAt       datetime2(3)  NOT NULL CONSTRAINT DF_RuntimeProbe_Created DEFAULT SYSUTCDATETIME(),
                    CompletedAt     datetime2(3)  NULL,
                    CONSTRAINT CK_RuntimeProbe_Status CHECK (Status IN ('Accepted', 'Running', 'Completed')));
            COMMIT;
            """, _ => { }, null, ct);
    }

    /// <summary>Creates the start record; false when the probe id already exists.</summary>
    public async Task<bool> TryCreateAsync(string probeId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("INSERT diagnostics.RuntimeProbe (ProbeId, Status) VALUES (@id, 'Accepted');",
                p => p.AddWithValue("@id", probeId), null, ct);
            return true;
        }
        catch (SqlException e) when (e.Number is 2627 or 2601)
        {
            return false;
        }
    }

    /// <summary>
    /// Conditional claim: a Completed probe is rejected (and the rejection counted); otherwise
    /// the claim count increases. More than one claim on a running probe is normal after a
    /// replay or restart; its effects are keyed and therefore not repeated.
    /// </summary>
    public async Task<ProbeClaimResult> ClaimAsync(string probeId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            UPDATE diagnostics.RuntimeProbe
            SET Claims = Claims + CASE WHEN Status = 'Completed' THEN 0 ELSE 1 END,
                FenceRejections = FenceRejections + CASE WHEN Status = 'Completed' THEN 1 ELSE 0 END,
                Status = CASE WHEN Status = 'Completed' THEN Status ELSE 'Running' END
            OUTPUT inserted.Status, inserted.Claims
            WHERE ProbeId = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", probeId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            // No start record: never run effects for an unknown probe.
            return new ProbeClaimResult(false, 0);
        }

        return new ProbeClaimResult(reader.GetString(0) != "Completed", reader.GetInt32(1));
    }

    /// <summary>Moves the probe to Completed once; returns false when it already was.</summary>
    public async Task<bool> CompleteAsync(string probeId, CancellationToken ct = default)
    {
        var rows = 0;
        await ExecuteAsync(
            "UPDATE diagnostics.RuntimeProbe SET Status = 'Completed', CompletedAt = SYSUTCDATETIME() WHERE ProbeId = @id AND Status <> 'Completed';",
            p => p.AddWithValue("@id", probeId), n => rows = n, ct);
        return rows == 1;
    }

    public async Task<ProbeRecord?> GetAsync(string probeId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT Status, Claims, FenceRejections, CreatedAt, CompletedAt FROM diagnostics.RuntimeProbe WHERE ProbeId = @id;", connection);
        command.Parameters.AddWithValue("@id", probeId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ProbeRecord(probeId, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4))
            : null;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task ExecuteAsync(string sql, Action<SqlParameterCollection> parameters, Action<int>? rows, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        parameters(command.Parameters);
        var n = await command.ExecuteNonQueryAsync(ct);
        rows?.Invoke(n);
    }
}

public sealed record ProbeRequest(int? TimerSeconds, int? EffectDelayMilliseconds);

public sealed record ProbeRecord(string ProbeId, string Status, int Claims, int FenceRejections, DateTime CreatedAt, DateTime? CompletedAt);
