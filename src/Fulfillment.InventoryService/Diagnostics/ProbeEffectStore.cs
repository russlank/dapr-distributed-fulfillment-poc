using Microsoft.Data.SqlClient;

namespace Fulfillment.InventoryService.Diagnostics;

/// <summary>
/// Probe effects in InventoryDb (schema diagnostics). One transaction records the attempt and
/// applies the effect at most once per operation key, the same shape as the ProcessedOperation
/// marker used by business operations from M2. Attempts show activity re-execution; effects
/// show that re-execution changed nothing. Schema is created at startup until migrations exist.
/// </summary>
public sealed class ProbeEffectStore(string connectionString)
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            IF SCHEMA_ID('diagnostics') IS NULL EXEC('CREATE SCHEMA diagnostics');
            IF OBJECT_ID('diagnostics.ProbeEffect') IS NULL
                CREATE TABLE diagnostics.ProbeEffect (
                    OperationKey nvarchar(200) NOT NULL CONSTRAINT PK_ProbeEffect PRIMARY KEY,
                    Replica      nvarchar(64)  NOT NULL,
                    AppliedAt    datetime2(3)  NOT NULL CONSTRAINT DF_ProbeEffect_At DEFAULT SYSUTCDATETIME());
            IF OBJECT_ID('diagnostics.ProbeEffectAttempt') IS NULL
                CREATE TABLE diagnostics.ProbeEffectAttempt (
                    Id           bigint IDENTITY CONSTRAINT PK_ProbeEffectAttempt PRIMARY KEY,
                    OperationKey nvarchar(200) NOT NULL,
                    Replica      nvarchar(64)  NOT NULL,
                    Applied      bit           NOT NULL,
                    At           datetime2(3)  NOT NULL CONSTRAINT DF_ProbeEffectAttempt_At DEFAULT SYSUTCDATETIME());
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Returns true when this attempt applied the effect, false when it already existed.</summary>
    public async Task<bool> RecordAsync(string operationKey, string replica, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await using var command = new SqlCommand(
            """
            DECLARE @applied bit = 0;
            IF NOT EXISTS (SELECT 1 FROM diagnostics.ProbeEffect WITH (UPDLOCK, HOLDLOCK) WHERE OperationKey = @key)
            BEGIN
                INSERT diagnostics.ProbeEffect (OperationKey, Replica) VALUES (@key, @replica);
                SET @applied = 1;
            END;
            INSERT diagnostics.ProbeEffectAttempt (OperationKey, Replica, Applied) VALUES (@key, @replica, @applied);
            SELECT @applied;
            """, connection, transaction);
        command.Parameters.AddWithValue("@key", operationKey);
        command.Parameters.AddWithValue("@replica", replica);
        var applied = (bool)(await command.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);
        return applied;
    }
}
