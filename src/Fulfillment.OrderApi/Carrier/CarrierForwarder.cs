using System.Diagnostics.Metrics;
using System.Text.Json;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Shipping;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Messaging;
using Fulfillment.OrderApi.Domain;
using Fulfillment.OrderApi.Orders;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Fulfillment.OrderApi.Carrier;

public sealed class CarrierForwarderOptions
{
    public const string Section = "CarrierForwarder";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public int BatchSize { get; set; } = 10;

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>UnknownShipment answers tolerated (the callback may precede Shipping's dispatch commit) before an AmbiguousDispatch case.</summary>
    public int UnknownShipmentRetries { get; set; } = 5;
}

/// <summary>Failpoints of the forwarder (drill builds only; key: event id, N format).</summary>
public static class ForwarderFailpoints
{
    /// <summary>Shipping committed and answered; the forward row is not yet marked (drills end the process here).</summary>
    public const string AfterForwardResponseBeforeMark = "AfterForwardResponseBeforeMark";
}

/// <summary>
/// Forwards accepted carrier callbacks to Shipping (runs in every order-api replica). Claims due forward rows with a
/// lease token, calls Shipping through Dapr service invocation (its answer is the processing outcome), then records
/// the outcome and acts on it: TerminalApplied while the order awaits delivery → a pending signal for the waiting
/// workflow (keyed by the event id); TerminalConflict → one CarrierTerminalConflict case (on a completed order a
/// history row only); UnknownShipment → retried, then an AmbiguousDispatch case; PayloadConflict → audit. Shipping
/// unreachable → retried with backoff, never dropped. A crash after Shipping answered repeats the call, which returns
/// Shipping's stored outcome, so the decision repeats too (each decision is idempotent).
/// </summary>
public sealed partial class CarrierForwarder(
    string connectionString,
    SidecarInvoker sidecar,
    OrderLifecycleSteps lifecycle,
    Failpoints failpoints,
    IOptions<CarrierForwarderOptions> options,
    RuntimeIdentity identity,
    ILogger<CarrierForwarder> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Fulfillment.OrderApi.Carrier");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("carrier_forward_outcomes_total", description: "Shipping's outcomes of forwarded carrier events");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Owner { get; } = $"{identity.Replica}/{Guid.NewGuid():N}"[..Math.Min(identity.Replica.Length + 33, 64)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or HttpRequestException)
            {
                LogFailed(logger, e.Message);
            }

            await Task.Delay(settings.PollInterval, stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        foreach (var claim in claimed)
        {
            await ForwardAsync(claim, ct);
        }

        return claimed.Count;
    }

    private sealed record Claim(Guid EventId, Guid Token, int Attempts, Guid ShipmentId, Guid OrderId, string Status, int Version, DateTime OccurredAt, string PayloadHash);

    private async Task<List<Claim>> ClaimAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var claim = new SqlCommand(
            """
            DECLARE @claimed TABLE (EventId uniqueidentifier, Token uniqueidentifier, Attempts int);
            UPDATE TOP (@n) f SET LeaseOwner = @owner, LeaseToken = NEWID(), LeaseExpiresAt = DATEADD(millisecond, @lease, SYSUTCDATETIME()), Attempts = Attempts + 1
            OUTPUT inserted.EventId, inserted.LeaseToken, inserted.Attempts INTO @claimed
            FROM orders.CarrierStatusForward f WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE f.State = 'Pending' AND f.NextAttemptAt <= SYSUTCDATETIME() AND (f.LeaseExpiresAt IS NULL OR f.LeaseExpiresAt < SYSUTCDATETIME());
            SELECT c.EventId, c.Token, c.Attempts, i.ShipmentId, i.OrderId, i.Status, i.Version, i.OccurredAt, i.PayloadHash
            FROM @claimed c JOIN orders.CarrierCallbackInbox i ON i.Source = 'carrier' AND i.EventId = c.EventId;
            """, connection);
        claim.Parameters.AddWithValue("@n", options.Value.BatchSize);
        claim.Parameters.AddWithValue("@owner", Owner);
        claim.Parameters.AddWithValue("@lease", (int)options.Value.Lease.TotalMilliseconds);
        var claims = new List<Claim>();
        await using var reader = await claim.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            claims.Add(new Claim(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5), reader.GetInt32(6),
                DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc), reader.GetString(8)));
        }

        return claims;
    }

    private async Task ForwardAsync(Claim c, CancellationToken ct)
    {
        var key = c.EventId.ToString("N");
        var ingest = new CarrierStatusIngest(c.EventId, c.ShipmentId, c.OrderId, c.Status, c.Version, c.OccurredAt, "callback", c.PayloadHash);
        var call = await sidecar.PostAsync(AppIds.ShippingService, ShippingRoutes.CarrierStatus.TrimStart('/'), ingest, key, ct);
        if (!call.IsSuccess || JsonSerializer.Deserialize<CarrierStatusIngested>(call.Body, Json) is not { } result)
        {
            await RetryLaterAsync(c, $"shipping {(int?)call.Status} {call.TransportError}", ct);
            return;
        }

        await failpoints.HitAsync(ForwarderFailpoints.AfterForwardResponseBeforeMark, key, ct);
        Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome));
        switch (result.Outcome)
        {
            case CarrierStatusOutcomes.UnknownShipment when c.Attempts <= options.Value.UnknownShipmentRetries:
                await RetryLaterAsync(c, "UnknownShipment", ct);
                return;
            case CarrierStatusOutcomes.UnknownShipment:
                await lifecycle.OpenCaseAsync(new OpenCaseRequest(c.OrderId, InterventionKinds.AmbiguousDispatch, "unknown-" + key,
                    $"carrier event {c.EventId} for shipment {c.ShipmentId} unknown to Shipping after {c.Attempts} attempts"), ct);
                break;
            case CarrierStatusOutcomes.TerminalConflict:
                await lifecycle.OpenCaseAsync(new OpenCaseRequest(c.OrderId, InterventionKinds.CarrierTerminalConflict, key,
                    $"carrier event {c.EventId} ({c.Status} v{c.Version}) after terminal {result.TerminalStatus}"), ct);
                break;
        }

        await MarkAsync(c, result, ct);
        LogForwarded(logger, c.EventId, c.ShipmentId, result.Outcome, result.Replayed);
    }

    /// <summary>Records Shipping's outcome (fenced by the lease token) with its signal, history, or audit decision.</summary>
    private async Task MarkAsync(Claim c, CarrierStatusIngested result, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var status = await OrderTransitions.LockAsync(connection, tx, c.OrderId, ct);
        await using (var mark = new SqlCommand(
                         """
                         UPDATE orders.CarrierStatusForward SET State = 'Processed', Outcome = @out, ProcessedAt = SYSUTCDATETIME(),
                             LeaseOwner = NULL, LeaseToken = NULL, LeaseExpiresAt = NULL, LastError = NULL
                         WHERE EventId = @e AND LeaseToken = @t AND State = 'Pending';
                         """, connection, tx))
        {
            mark.Parameters.AddWithValue("@out", result.Outcome);
            mark.Parameters.AddWithValue("@e", c.EventId);
            mark.Parameters.AddWithValue("@t", c.Token);
            if (await mark.ExecuteNonQueryAsync(ct) != 1)
            {
                // The lease was lost (another forwarder took over); that one records the same stored outcome.
                await tx.RollbackAsync(ct);
                return;
            }
        }

        if (result.Outcome == CarrierStatusOutcomes.TerminalApplied && status is { Status: var s })
        {
            if (s == OrderStatus.AwaitingDelivery)
            {
                await using var signal = new SqlCommand(
                    """
                    INSERT orders.PendingSignal (OrderId, InstanceId, EventName, CausedBy, State, Attempts)
                    SELECT @o, @i, @n, @e, 'Pending', 0
                    WHERE NOT EXISTS (SELECT 1 FROM orders.PendingSignal WHERE CausedBy = @e AND EventName = @n);
                    """, connection, tx);
                signal.Parameters.AddWithValue("@o", c.OrderId);
                signal.Parameters.AddWithValue("@i", FulfillmentWorkflow.InstanceId(c.OrderId));
                signal.Parameters.AddWithValue("@n", CarrierSignals.EventName(c.ShipmentId));
                signal.Parameters.AddWithValue("@e", c.EventId);
                await signal.ExecuteNonQueryAsync(ct);
            }
            else if (OrderStatuses.IsTerminal(s))
            {
                await OrderLifecycleSteps.AppendHistoryAsync(connection, tx, c.OrderId, s, s, OrderReasonCodes.CarrierStatusAfterTerminal, ct);
            }
        }
        else if (result.Outcome == CarrierStatusOutcomes.PayloadConflict)
        {
            await using var audit = new SqlCommand(
                "INSERT orders.CarrierCallbackConflict (EventId, StoredHash, ReceivedHash, DetectedBy) VALUES (@e, '', @h, 'shipping')", connection, tx);
            audit.Parameters.AddWithValue("@e", c.EventId);
            audit.Parameters.AddWithValue("@h", c.PayloadHash);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private async Task RetryLaterAsync(Claim c, string error, CancellationToken ct)
    {
        var backoff = Math.Min(options.Value.MaxBackoff.TotalMilliseconds, 500 * Math.Pow(2, Math.Min(c.Attempts, 10)));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var update = new SqlCommand(
            """
            UPDATE orders.CarrierStatusForward SET LeaseOwner = NULL, LeaseToken = NULL, LeaseExpiresAt = NULL, LastError = LEFT(@err, 200),
                NextAttemptAt = DATEADD(millisecond, @b, SYSUTCDATETIME())
            WHERE EventId = @e AND LeaseToken = @t AND State = 'Pending';
            """, connection);
        update.Parameters.AddWithValue("@err", error);
        update.Parameters.AddWithValue("@b", (int)backoff);
        update.Parameters.AddWithValue("@e", c.EventId);
        update.Parameters.AddWithValue("@t", c.Token);
        await update.ExecuteNonQueryAsync(ct);
        LogRetry(logger, c.EventId, error, backoff);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "carrier event {EventId} for {ShipmentId} forwarded: {Outcome} (replayed {Replayed})")]
    private static partial void LogForwarded(ILogger logger, Guid eventId, Guid shipmentId, string outcome, bool replayed);

    [LoggerMessage(Level = LogLevel.Information, Message = "carrier event {EventId}: forward retried later ({Error}, in {BackoffMs} ms)")]
    private static partial void LogRetry(ILogger logger, Guid eventId, string error, double backoffMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "carrier forwarder: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
