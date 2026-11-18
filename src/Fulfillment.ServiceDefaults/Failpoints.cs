using Microsoft.Extensions.Logging;
#if FULFILLMENT_FAILPOINTS
using Microsoft.Data.SqlClient;
#endif

namespace Fulfillment.ServiceDefaults;

/// <summary>
/// Test-only failure injection at named boundaries (drill builds only).
/// Release builds compile every hit to a no-op and contain no store type; drill builds act only
/// when DAPR_POC_ENV=drill. Arming is a row in the owning database's drill.Failpoint table
/// (written by the drill script); firing is a one-shot conditional UPDATE whose FiredAt value is
/// the persisted proof that the window was reached.
/// </summary>
public sealed partial class Failpoints
{
#if FULFILLMENT_FAILPOINTS
    public const bool Compiled = true;
#else
    public const bool Compiled = false;
#endif

    private readonly string? connectionString;
    private readonly ILogger<Failpoints> logger;

    public Failpoints(string? connectionString, ILogger<Failpoints> logger)
    {
        this.connectionString = connectionString;
        this.logger = logger;
    }

    /// <summary>True when failpoints are compiled in and the process runs in the drill environment.</summary>
    public static bool Active =>
        Compiled && string.Equals(Environment.GetEnvironmentVariable("DAPR_POC_ENV"), "drill", StringComparison.Ordinal);

#if FULFILLMENT_FAILPOINTS
    /// <summary>Creates drill.Failpoint in the owning database (drill builds only).</summary>
    public async Task EnsureStoreAsync(CancellationToken ct = default)
    {
        if (!Active || connectionString is null)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            IF SCHEMA_ID('drill') IS NULL EXEC('CREATE SCHEMA drill');
            IF OBJECT_ID('drill.Failpoint') IS NULL
                CREATE TABLE drill.Failpoint (
                    Name nvarchar(120) NOT NULL, DrillRunId nvarchar(80) NOT NULL, OperationKey nvarchar(200) NOT NULL,
                    Action nvarchar(20) NOT NULL, ArmedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), FiredAt datetime2(3) NULL,
                    CONSTRAINT PK_drill_Failpoint PRIMARY KEY (Name, OperationKey, DrillRunId));
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Fires an armed failpoint once; FailFast ends the process, Throw raises a transient error.</summary>
    public async Task HitAsync(string name, string operationKey, CancellationToken ct = default)
    {
        if (!Active || connectionString is null)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            UPDATE TOP (1) drill.Failpoint SET FiredAt = SYSUTCDATETIME()
            OUTPUT inserted.Action, inserted.DrillRunId
            WHERE Name = @name AND OperationKey = @key AND FiredAt IS NULL;
            """, connection);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@key", operationKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return;
        }

        var action = reader.GetString(0);
        LogFired(logger, name, operationKey, reader.GetString(1), action);
        switch (action)
        {
            case "FailFast":
                Environment.FailFast($"failpoint {name} fired for {operationKey}");
                break;
            case "Throw":
                throw new InvalidOperationException($"failpoint {name} fired for {operationKey}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "failpoint {Name} fired for {OperationKey} (run {RunId}, action {Action})")]
    private static partial void LogFired(ILogger logger, string name, string operationKey, string runId, string action);
#else
    /// <summary>No-op in release builds.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Same instance API as drill builds.")]
    public Task EnsureStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op in release builds.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Same instance API as drill builds.")]
    public Task HitAsync(string name, string operationKey, CancellationToken ct = default) => Task.CompletedTask;
#endif

    public static string Describe() => Compiled ? (Active ? "compiled, active" : "compiled, inactive") : "absent";
}
