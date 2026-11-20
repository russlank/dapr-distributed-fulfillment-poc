using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Fulfillment.Primitives;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Orders;

/// <summary>Failpoints of the lifecycle steps (drill builds only).</summary>
public static class LifecycleCheckpoints
{
    /// <summary>Inside the BeginDispatch transaction, after the order is locked and before the dispatch start is written (drills hold it here with a delay).</summary>
    public const string BeforeBeginDispatchCommit = "BeforeBeginDispatchCommit";
}

/// <summary>
/// OrderDb steps of the fulfillment after reservation: forward status steps, the cancellation point of no
/// return, and intervention cases. Each locks the order row first (all steps of one order are serialized), is
/// conditional on the order's state, and answers a repeated execution from the stored state.
/// </summary>
public sealed class OrderLifecycleSteps(string connectionString, StepCheckpoint checkpoint)
{
    /// <summary>The forward steps of a running fulfillment and their reasons; any other step is refused.</summary>
    private static readonly Dictionary<OrderStatus, (OrderStatus From, string Reason)> Forward = new()
    {
        [OrderStatus.PaymentAuthorized] = (OrderStatus.Reserving, OrderReasonCodes.PaymentAuthorized),
        [OrderStatus.Packing] = (OrderStatus.PaymentAuthorized, OrderReasonCodes.PackingStarted),
        [OrderStatus.Dispatched] = (OrderStatus.Packing, OrderReasonCodes.ShipmentDispatched),
        [OrderStatus.AwaitingDelivery] = (OrderStatus.Dispatched, OrderReasonCodes.DeliveryAwaited),
    };

    public async Task<StepResult> AdvanceAsync(AdvanceFulfillmentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var to = Enum.Parse<OrderStatus>(request.ToStatus);
        if (!Forward.TryGetValue(to, out var step))
        {
            throw new ArgumentException($"not a forward fulfillment step: {request.ToStatus}", nameof(request));
        }

        var key = OperationKey.For(request.OrderId, "advance", to.ToString()).Value;
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyTerminal, key, status.ToString());
        }

        if (status == to || IsLater(status, to))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyApplied, key, status.ToString());
        }

        if (!await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, step.From, to, step.Reason, checkpoint, key, ct))
        {
            throw new InvalidOperationException($"fulfillment step to {to} of an order in status {status}");
        }

        await tx.CommitAsync(ct);
        return new StepResult(StepOutcomes.Applied, key, to.ToString());
    }

    /// <summary>
    /// The cancellation point of no return (I15): records the dispatch start only if no cancellation was recorded;
    /// the cancellation endpoint records a cancellation only if dispatch has not started. Both lock the order row,
    /// and the database refuses a row with both (CK_Order_CancelOrDispatch).
    /// </summary>
    public async Task<BeginDispatchResult> BeginDispatchAsync(BeginDispatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = OperationKey.For(request.OrderId, "dispatch-start", "1").Value;
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new BeginDispatchResult(DispatchOutcomes.AlreadyTerminal, status.ToString());
        }

        await checkpoint(LifecycleCheckpoints.BeforeBeginDispatchCommit, key, ct);
        await using var update = new SqlCommand(
            """
            UPDATE orders.[Order] SET DispatchStartedAt = SYSUTCDATETIME()
            WHERE OrderId = @o AND CancelRequestedAt IS NULL AND DispatchStartedAt IS NULL AND Status = 'Packing';
            SELECT CASE WHEN CancelRequestedAt IS NOT NULL THEN 'CancelRequested' WHEN DispatchStartedAt IS NOT NULL THEN 'Started' ELSE 'None' END
            FROM orders.[Order] WHERE OrderId = @o;
            """, connection, tx);
        update.Parameters.AddWithValue("@o", request.OrderId);
        var changed = 0;
        string state;
        await using (var reader = await update.ExecuteReaderAsync(ct))
        {
            changed = reader.RecordsAffected;
            await reader.ReadAsync(ct);
            state = reader.GetString(0);
        }

        await tx.CommitAsync(ct);
        return state switch
        {
            "CancelRequested" => new BeginDispatchResult(DispatchOutcomes.CancelRequested, status.ToString()),
            "Started" => new BeginDispatchResult(changed == 1 ? DispatchOutcomes.Started : DispatchOutcomes.AlreadyStarted, status.ToString()),
            _ => throw new InvalidOperationException($"dispatch start of an order in status {status}"),
        };
    }

    /// <summary>
    /// Opens a case once per idempotency key. A non-terminal order moves to ManualInterventionRequired (its status is
    /// kept to resume to); a terminal order only gets a history row. A second open case of the same kind for the order
    /// is refused by the database; the existing one is returned.
    /// </summary>
    public async Task<CaseResult> OpenCaseAsync(OpenCaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = OperationKey.For(request.OrderId, "case-" + request.Kind, request.Qualifier).Value;
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        var existing = await ReadCaseByAsync(connection, tx, "OperationKey = @v", key, ct)
            ?? await ReadCaseByAsync(connection, tx, "OrderId = @o AND Kind = @k AND State IN ('Open', 'Reconciling')", null, ct, request.OrderId, request.Kind);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return existing;
        }

        var terminal = OrderStatuses.IsTerminal(status);
        Guid caseId;
        await using (var insert = new SqlCommand(
                         """
                         INSERT orders.InterventionCase (OrderId, Kind, State, OperationKey, Evidence, ResumeStatus)
                         OUTPUT inserted.CaseId VALUES (@o, @k, 'Open', @key, @e, @resume);
                         """, connection, tx))
        {
            insert.Parameters.AddWithValue("@o", request.OrderId);
            insert.Parameters.AddWithValue("@k", request.Kind);
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@e", request.Evidence.Length > 2000 ? request.Evidence[..2000] : request.Evidence);
            insert.Parameters.AddWithValue("@resume", terminal ? DBNull.Value : status.ToString());
            caseId = (Guid)(await insert.ExecuteScalarAsync(ct))!;
        }

        if (terminal)
        {
            await AppendHistoryAsync(connection, tx, request.OrderId, status, status, OrderReasonCodes.PostTerminalCase, ct);
        }
        else if (status != OrderStatus.ManualInterventionRequired
                 && !await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, status, OrderStatus.ManualInterventionRequired,
                     OrderReasonCodes.InterventionRequired, checkpoint, key, ct))
        {
            throw new InvalidOperationException($"opening a case for an order in status {status}");
        }

        await tx.CommitAsync(ct);
        var opened = await ReadCaseAsync(new ReadCaseRequest(caseId), ct);
        return opened with { Created = true };
    }

    public async Task<CaseResult> ReadCaseAsync(ReadCaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var found = await ReadCaseByAsync(connection, tx, "CaseId = @v", request.CaseId, ct)
            ?? throw new InvalidOperationException($"case {request.CaseId} does not exist");
        await tx.CommitAsync(ct);
        return found;
    }

    private static async Task<CaseResult?> ReadCaseByAsync(SqlConnection connection, SqlTransaction tx, string where, object? value, CancellationToken ct,
        Guid? orderId = null, string? kind = null)
    {
        await using var read = new SqlCommand(
            $"""
             SELECT TOP (1) c.CaseId, c.Kind, c.State, c.ResolutionAction, o.Status
             FROM orders.InterventionCase c JOIN orders.[Order] o ON o.OrderId = c.OrderId
             WHERE {where.Replace("OrderId", "c.OrderId", StringComparison.Ordinal)} ORDER BY c.OpenedAt;
             """, connection, tx);
        if (value is not null)
        {
            read.Parameters.AddWithValue("@v", value);
        }

        if (orderId is not null)
        {
            read.Parameters.AddWithValue("@o", orderId.Value);
            read.Parameters.AddWithValue("@k", kind!);
        }

        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var orderStatus = Enum.Parse<OrderStatus>(reader.GetString(4));
        return new CaseResult(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            orderStatus.ToString(), OrderStatuses.IsTerminal(orderStatus), false);
    }

    internal static async Task AppendHistoryAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, OrderStatus from, OrderStatus to, string reason, CancellationToken ct)
    {
        await using var history = new SqlCommand(
            """
            INSERT orders.OrderStatusHistory (OrderId, Sequence, FromStatus, ToStatus, ReasonCode)
            SELECT @o, ISNULL(MAX(Sequence), 0) + 1, @from, @to, @reason
            FROM orders.OrderStatusHistory WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @o;
            """, connection, tx);
        history.Parameters.AddWithValue("@o", orderId);
        history.Parameters.AddWithValue("@from", from.ToString());
        history.Parameters.AddWithValue("@to", to.ToString());
        history.Parameters.AddWithValue("@reason", reason);
        await history.ExecuteNonQueryAsync(ct);
    }

    private static readonly OrderStatus[] Chain = [OrderStatus.Reserving, OrderStatus.PaymentAuthorized, OrderStatus.Packing, OrderStatus.Dispatched, OrderStatus.AwaitingDelivery];

    /// <summary>True when the order is already past <paramref name="to"/> in the forward chain.</summary>
    private static bool IsLater(OrderStatus status, OrderStatus to) => Array.IndexOf(Chain, status) > Array.IndexOf(Chain, to);

    /// <summary>
    /// The terminal transaction of a delivered order: AwaitingDelivery → Completed (Delivered), the OrderCompleted
    /// outbox row, and the start request closed, all or nothing. The outbox row exists once per order, so a repeat
    /// finds it and changes nothing.
    /// </summary>
    public async Task<StepResult> FinalizeDeliveredAsync(CompleteFulfillmentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = OperationKey.For(request.OrderId, "finalize", "1").Value;
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        await using (var exists = new SqlCommand("SELECT COUNT(*) FROM orders.OrderOutbox WHERE OrderId = @o AND EventType = 'OrderCompleted'", connection, tx))
        {
            exists.Parameters.AddWithValue("@o", request.OrderId);
            if ((int)(await exists.ExecuteScalarAsync(ct))! > 0)
            {
                await tx.CommitAsync(ct);
                return new StepResult(StepOutcomes.AlreadyApplied, key, status.ToString());
            }
        }

        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyTerminal, key, status.ToString());
        }

        if (!await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.AwaitingDelivery, OrderStatus.Completed, OrderReasonCodes.Delivered,
                checkpoint, key, ct))
        {
            throw new InvalidOperationException($"finalizing an order in status {status}");
        }

        await using (var outbox = new SqlCommand(
                         "INSERT orders.OrderOutbox (OrderId, EventType, Payload, State) VALUES (@o, 'OrderCompleted', @p, 'Pending')", connection, tx))
        {
            outbox.Parameters.AddWithValue("@o", request.OrderId);
            outbox.Parameters.AddWithValue("@p", $"{{\"orderId\":\"{request.OrderId}\",\"instanceId\":\"{request.InstanceId}\"}}");
            await outbox.ExecuteNonQueryAsync(ct);
        }

        await OrderTransitions.CloseStartRequestAsync(connection, tx, request.OrderId, ct);
        await tx.CommitAsync(ct);
        return new StepResult(StepOutcomes.Applied, key, nameof(OrderStatus.Completed));
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
