using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Interventions;

/// <summary>A repair start request: written in the resolution transaction, started by the repair dispatcher.</summary>
public sealed class RepairStartRequest
{
    public Guid CaseId { get; private set; }

    public Guid OrderId { get; private set; }

    public string InstanceId { get; private set; } = "";

    /// <summary>Pending, Started, or Closed (the repair recorded its outcome).</summary>
    public string State { get; private set; } = "";

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAt { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public string? LastError { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }
}

/// <summary>
/// Starts repair workflows for resolved cases (runs in every order-api replica): claims a due Pending request with a
/// lease, starts repair-{order}-{case} through the workflow host's management API (state first: an instance that
/// exists already counts as started), and marks it Started; anything else is retried with backoff. The repair
/// workflow's effects use repair operation keys, so it can never repeat the original effects.
/// </summary>
public sealed partial class RepairDispatcher(string connectionString, IServiceScopeFactory scopes, RuntimeIdentity identity, ILogger<RepairDispatcher> logger) : BackgroundService
{
    private string Owner { get; } = $"{identity.Replica}/repair"[..Math.Min(identity.Replica.Length + 7, 64)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await StartNextAsync(stoppingToken))
                {
                }
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException)
            {
                LogFailed(logger, e.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    public async Task<bool> StartNextAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        RepairInput input;
        Guid token;
        string instanceId;
        await using (var claim = new SqlCommand(
                         """
                         DECLARE @c TABLE (CaseId uniqueidentifier, OrderId uniqueidentifier, InstanceId varchar(80), Token uniqueidentifier);
                         UPDATE TOP (1) r SET LeaseOwner = @owner, LeaseToken = NEWID(), LeaseExpiresAt = DATEADD(second, 30, SYSUTCDATETIME()), Attempts = Attempts + 1
                         OUTPUT inserted.CaseId, inserted.OrderId, inserted.InstanceId, inserted.LeaseToken INTO @c
                         FROM orders.RepairStartRequest r WITH (UPDLOCK, READPAST, ROWLOCK)
                         WHERE r.State = 'Pending' AND r.NextAttemptAt <= SYSUTCDATETIME() AND (r.LeaseExpiresAt IS NULL OR r.LeaseExpiresAt < SYSUTCDATETIME());
                         SELECT c.CaseId, c.OrderId, c.InstanceId, c.Token, k.Kind, k.ResolutionAction,
                                ISNULL((SELECT TotalMinor FROM orders.OrderCheck WHERE OrderId = c.OrderId AND CheckName = 'price'), 0),
                                ISNULL((SELECT Currency FROM orders.OrderCheck WHERE OrderId = c.OrderId AND CheckName = 'price'), 'EUR')
                         FROM @c c JOIN orders.InterventionCase k ON k.CaseId = c.CaseId;
                         """, connection))
        {
            claim.Parameters.AddWithValue("@owner", Owner);
            await using var reader = await claim.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return false;
            }

            input = new RepairInput(reader.GetGuid(1), reader.GetGuid(0), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7));
            instanceId = reader.GetString(2);
            token = reader.GetGuid(3);
        }

        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var workflows = scope.ServiceProvider.GetRequiredService<WorkflowHostClient>();
            await workflows.StartAsync(RepairWorkflow.Name, instanceId, input, replaceTerminal: false, ct);
        }
        catch (WorkflowHostUnavailableException e)
        {
            error = e.Message;
        }

        await using var mark = new SqlCommand(
            """
            UPDATE orders.RepairStartRequest
            SET State = CASE WHEN @err IS NULL THEN 'Started' ELSE State END, StartedAt = CASE WHEN @err IS NULL THEN SYSUTCDATETIME() ELSE StartedAt END,
                LastError = LEFT(@err, 200), NextAttemptAt = DATEADD(second, CASE WHEN @err IS NULL THEN 0 ELSE 5 END, SYSUTCDATETIME()),
                LeaseOwner = NULL, LeaseToken = NULL, LeaseExpiresAt = NULL
            WHERE CaseId = @c AND LeaseToken = @t AND State = 'Pending';
            """, connection);
        mark.Parameters.AddWithValue("@err", (object?)error ?? DBNull.Value);
        mark.Parameters.AddWithValue("@c", input.CaseId);
        mark.Parameters.AddWithValue("@t", token);
        await mark.ExecuteNonQueryAsync(ct);
        LogStarted(logger, instanceId, error ?? "started");
        return error is null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "repair {InstanceId}: {Result}")]
    private static partial void LogStarted(ILogger logger, string instanceId, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "repair dispatcher: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
