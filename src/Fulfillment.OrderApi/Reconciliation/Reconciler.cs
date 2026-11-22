using System.Diagnostics.Metrics;
using Fulfillment.Contracts.Events;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Fulfillment.OrderApi.Events;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Fulfillment.OrderApi.Reconciliation;

public sealed class ReconcilerOptions
{
    public const string Section = "Reconciler";

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>An order whose status has not changed for this long has its workflow checked.</summary>
    public TimeSpan StageThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A review wait this long past its deadline (plus the timer grace) gets a nudge.</summary>
    public TimeSpan ReviewNudgeAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A projection gap or lag older than this is repaired by an outbox replay.</summary>
    public TimeSpan GapThreshold { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How far back the source heads are compared with current state.</summary>
    public TimeSpan HeadsWindow { get; set; } = TimeSpan.FromHours(24);

    public int MaxOrdersPerPass { get; set; } = 200;

    public static void ForEnvironment(ReconcilerOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.Interval = TimeSpan.FromSeconds(5);
            options.Lease = TimeSpan.FromSeconds(15);
            options.StageThreshold = TimeSpan.FromSeconds(20);
            options.ReviewNudgeAfter = TimeSpan.FromSeconds(60);
            options.GapThreshold = TimeSpan.FromSeconds(10);
            options.HeadsWindow = TimeSpan.FromMinutes(30);
        }
    }
}

/// <summary>
/// The reconciler (plan 7.9): one leased instance across the order API replicas. It reads, and acts only through
/// existing mechanisms: intervention cases, pending signals, and outbox replay. Checks per pass:
/// <list type="bullet">
/// <item>non-terminal orders without progress past the stage threshold and without a case: the workflow failed or
/// was terminated (WorkflowFaulted case), is missing although started (WorkflowMissing), or completed without
/// finalizing (ExecutionMismatch); one case per order and kind, ever;</item>
/// <item>a running workflow waiting for a review whose deadline passed long ago (a lost timer): a nudge, which is
/// the review's own signal; the workflow re-reads SQL when woken;</item>
/// <item>projection gaps older than the gap threshold, and streams whose source has published more than current
/// state shows (a lost last event): replay from the last applied sequence plus one; a source without the
/// missing event opens a ProjectionGap case.</item>
/// </list>
/// </summary>
public sealed partial class Reconciler(string connectionString, WorkflowHostClient workflows, OutboxReplayClient outbox, IOptions<ReconcilerOptions> options,
    RuntimeIdentity identity, ILogger<Reconciler> logger) : BackgroundService
{
    private const string LeaseName = "reconciler";
    private static readonly Meter Meter = new("Fulfillment.Reconciler");
    private static readonly Counter<long> Actions = Meter.CreateCounter<long>("reconciler_actions_total");

    private readonly string owner = $"{identity.Replica}/{Guid.NewGuid():N}";
    private readonly Dictionary<(Guid OrderId, string Source), DateTime> replayRequested = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await TryLeaseAsync(stoppingToken))
                {
                    await RunPassAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, e.Message);
            }

            try
            {
                await Task.Delay(o.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass of every check (the lease must be held).</summary>
    public async Task RunPassAsync(CancellationToken ct)
    {
        await CheckWorkflowsAsync(ct);
        await CheckProjectionAsync(ct);
        await ExecAsync("UPDATE orders.ReconcilerLease SET LastRunAt = SYSUTCDATETIME() WHERE Name = @n AND Owner = @me", ct, ("@n", LeaseName), ("@me", owner));
    }

    private async Task<bool> TryLeaseAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM orders.ReconcilerLease WITH (UPDLOCK, HOLDLOCK) WHERE Name = @n)
                INSERT orders.ReconcilerLease (Name) VALUES (@n);
            UPDATE orders.ReconcilerLease SET Owner = @me, LeaseUntil = DATEADD(millisecond, @lease, SYSUTCDATETIME())
            WHERE Name = @n AND (Owner = @me OR LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME());
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.AddWithValue("@n", LeaseName);
        command.Parameters.AddWithValue("@me", owner);
        command.Parameters.AddWithValue("@lease", (int)options.Value.Lease.TotalMilliseconds);
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }

    private async Task CheckWorkflowsAsync(CancellationToken ct)
    {
        var o = options.Value;
        var candidates = new List<(Guid OrderId, string Status)>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(
                """
                SELECT TOP (@n) o.OrderId, o.Status
                FROM orders.[Order] o
                JOIN orders.WorkflowStartRequest r ON r.OrderId = o.OrderId
                WHERE o.Status NOT IN ('Completed', 'Rejected', 'Compensated', 'Cancelled', 'Escalated')
                  AND r.State <> 'Pending'
                  AND (SELECT MAX(h.OccurredAt) FROM orders.OrderStatusHistory h WHERE h.OrderId = o.OrderId) < DATEADD(millisecond, -@stage, SYSUTCDATETIME())
                  AND NOT EXISTS (SELECT 1 FROM orders.InterventionCase c WHERE c.OrderId = o.OrderId AND c.State IN ('Open', 'Reconciling', 'Escalated'))
                  -- Before escalation existed, a failed delivery resolved without success ended the run while the order
                  -- awaited delivery; such orders are surfaced by their case, not a mismatch. (Now the order is Escalated.)
                  AND NOT EXISTS (SELECT 1 FROM orders.InterventionCase c WHERE c.OrderId = o.OrderId AND c.State = 'Resolved'
                                  AND c.Kind IN ('DeliveryException', 'DeliveryOverdue'))
                ORDER BY o.OrderId;
                """, connection);
            command.Parameters.AddWithValue("@n", o.MaxOrdersPerPass);
            command.Parameters.AddWithValue("@stage", (int)o.StageThreshold.TotalMilliseconds);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidates.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var (orderId, status) in candidates)
        {
            WorkflowStateView state;
            try
            {
                state = await workflows.GetStateAsync(FulfillmentWorkflow.InstanceId(orderId), ct);
            }
            catch (WorkflowHostUnavailableException)
            {
                // Unknown is not missing: try again next pass.
                return;
            }

            var kind = !state.Exists ? InterventionKinds.WorkflowMissing
                : state.Status is "Failed" or "Terminated" ? InterventionKinds.WorkflowFaulted
                : state.Status == "Completed" ? InterventionKinds.ExecutionMismatch
                : null;
            if (kind is not null)
            {
                if (await OpenCaseAsync(orderId, kind, status, $"workflow {state.Status ?? "not found"}; order {status}", ct))
                {
                    Actions.Add(1, new KeyValuePair<string, object?>("action", kind));
                    LogCaseOpened(logger, orderId, kind, state.Status ?? "not found", status);
                }

                continue;
            }

            if (status == nameof(OrderStatus.AwaitingApproval) && await NudgeReviewAsync(orderId, ct))
            {
                Actions.Add(1, new KeyValuePair<string, object?>("action", "Nudge"));
                LogNudged(logger, orderId);
            }
            else
            {
                Actions.Add(1, new KeyValuePair<string, object?>("action", "OrderProgressStalled"));
            }
        }
    }

    /// <summary>Opens one case per order and kind (never a second one, also after a resolution); the order's status is kept.</summary>
    private async Task<bool> OpenCaseAsync(Guid orderId, string kind, string status, string evidence, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await Orders.OrderTransitions.LockAsync(connection, tx, orderId, ct);
        await using var command = new SqlCommand(
            """
            INSERT orders.InterventionCase (OrderId, Kind, State, OperationKey, Evidence, ResumeStatus)
            SELECT @o, @k, 'Open', @key, @e, @resume
            WHERE NOT EXISTS (SELECT 1 FROM orders.InterventionCase WHERE OrderId = @o AND Kind = @k)
              AND EXISTS (SELECT 1 FROM orders.[Order] WHERE OrderId = @o AND Status = @resume);
            SELECT @@ROWCOUNT;
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@k", kind);
        command.Parameters.AddWithValue("@key", $"{orderId:N}:reconciler:{kind}");
        command.Parameters.AddWithValue("@e", evidence);
        command.Parameters.AddWithValue("@resume", status);
        var opened = (int)(await command.ExecuteScalarAsync(ct))! == 1;
        await tx.CommitAsync(ct);
        return opened;
    }

    /// <summary>
    /// A review round still Pending well past its deadline while the workflow runs: its timer was lost. The nudge is
    /// the review's own signal (pending signal, delivered by the dispatcher), at most one pending at a time.
    /// </summary>
    private async Task<bool> NudgeReviewAsync(Guid orderId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            INSERT orders.PendingSignal (OrderId, InstanceId, EventName, CausedBy, State, Attempts)
            SELECT TOP (1) r.OrderId, @i, 'ReviewSignal_' + LOWER(REPLACE(CONVERT(varchar(36), r.ReviewId), '-', '')), NEWID(), 'Pending', 0
            FROM orders.ReviewTask r
            WHERE r.OrderId = @o AND r.Status = 'Pending' AND r.DeadlineAt < DATEADD(millisecond, -@after, SYSUTCDATETIME())
              AND NOT EXISTS (SELECT 1 FROM orders.PendingSignal s WHERE s.OrderId = @o AND s.State = 'Pending')
            ORDER BY r.Round DESC;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@i", FulfillmentWorkflow.InstanceId(orderId));
        command.Parameters.AddWithValue("@after", (int)options.Value.ReviewNudgeAfter.TotalMilliseconds);
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }

    private async Task CheckProjectionAsync(CancellationToken ct)
    {
        var o = options.Value;
        var lagging = new Dictionary<(Guid OrderId, string Source), int>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await using var gaps = new SqlCommand(
                """
                SELECT DISTINCT OrderId, Source, LastAppliedSequence FROM orders.OrderEventGaps
                WHERE OldestPendingAt < DATEADD(millisecond, -@gap, SYSUTCDATETIME());
                """, connection);
            gaps.Parameters.AddWithValue("@gap", (int)o.GapThreshold.TotalMilliseconds);
            await using var reader = await gaps.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                lagging[(reader.GetGuid(0), reader.GetString(1))] = reader.GetInt32(2);
            }
        }

        // Lost last events leave no pending row behind: compare what each source has published (and settled) with
        // current state.
        var since = DateTime.UtcNow - o.HeadsWindow;
        var settled = DateTime.UtcNow - o.GapThreshold;
        foreach (var source in EventSources.All)
        {
            var heads = await outbox.HeadsAsync(source, since, settled, ct);
            if (heads is null)
            {
                continue;
            }

            var applied = await AppliedAsync(source, heads.Select(h => h.OrderId).ToList(), ct);
            foreach (var head in heads.Where(h => h.MaxPublishedSequence > applied.GetValueOrDefault(h.OrderId)))
            {
                lagging.TryAdd((head.OrderId, source), applied.GetValueOrDefault(head.OrderId));
            }
        }

        foreach (var ((orderId, source), lastApplied) in lagging)
        {
            if (replayRequested.TryGetValue((orderId, source), out var at) && DateTime.UtcNow - at < o.GapThreshold)
            {
                continue;
            }

            replayRequested[(orderId, source)] = DateTime.UtcNow;
            var result = await outbox.ReplayAsync(source, new OutboxReplayRequest(orderId, lastApplied + 1), ct);
            Actions.Add(1, new KeyValuePair<string, object?>("action", "OutboxReplay"));
            LogReplay(logger, orderId, source, lastApplied + 1, result?.Replayed ?? -1, result?.MaxSequence ?? -1);
            if (result is not null && result.MaxSequence < lastApplied + 1)
            {
                // The source does not have the missing event: never skipped, surfaced instead.
                LogGapUnresolvable(logger, orderId, source, lastApplied + 1);
                Actions.Add(1, new KeyValuePair<string, object?>("action", "ProjectionGapUnresolvable"));
                await OpenGapCaseAsync(orderId, source, lastApplied + 1, ct);
            }
        }
    }

    private async Task OpenGapCaseAsync(Guid orderId, string source, int missing, CancellationToken ct) =>
        await ExecAsync(
            """
            INSERT orders.InterventionCase (OrderId, Kind, State, OperationKey, Evidence, ResumeStatus)
            SELECT @o, 'ProjectionGap', 'Open', @key, @e, NULL
            WHERE EXISTS (SELECT 1 FROM orders.[Order] WHERE OrderId = @o)
              AND NOT EXISTS (SELECT 1 FROM orders.InterventionCase WHERE OrderId = @o AND Kind = 'ProjectionGap' AND State IN ('Open', 'Reconciling'));
            """, ct, ("@o", orderId), ("@key", $"{orderId:N}:projection:{source}:{missing}"), ("@e", $"source {source} has no event {missing}"));

    private async Task<Dictionary<Guid, int>> AppliedAsync(string source, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var applied = new Dictionary<Guid, int>();
        foreach (var chunk in orderIds.Chunk(500))
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(
                $"SELECT OrderId, LastAppliedSequence FROM orders.OrderTimelineState WHERE Source = @s AND OrderId IN ({string.Join(",", chunk.Select(o => $"'{o:D}'"))})",
                connection);
            command.Parameters.AddWithValue("@s", source);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                applied[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        return applied;
    }

    private async Task ExecAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "reconciler: {Kind} case opened for order {OrderId} (workflow {Workflow}, order {Status})")]
    private static partial void LogCaseOpened(ILogger logger, Guid orderId, string kind, string workflow, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "reconciler: review of order {OrderId} nudged (deadline long past while the workflow waits)")]
    private static partial void LogNudged(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "reconciler: replay of {Source} for order {OrderId} from {From}: replayed {Replayed}, source max {Max}")]
    private static partial void LogReplay(ILogger logger, Guid orderId, string source, int from, int replayed, int max);

    [LoggerMessage(Level = LogLevel.Error, Message = "reconciler: ProjectionGapUnresolvable order {OrderId} source {Source}: the source has no event {Missing}")]
    private static partial void LogGapUnresolvable(ILogger logger, Guid orderId, string source, int missing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "reconciler: pass failed: {Error}")]
    private static partial void LogPassFailed(ILogger logger, string error);
}
