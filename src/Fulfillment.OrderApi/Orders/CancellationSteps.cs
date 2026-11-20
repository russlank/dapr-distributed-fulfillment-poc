using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Orders;

public enum CancelOutcome
{
    /// <summary>The cancellation was recorded now; the fulfillment ends Cancelled at its next check.</summary>
    Recorded,

    /// <summary>A cancellation was recorded before (or the order is already Cancelled): nothing changed.</summary>
    AlreadyRequested,

    /// <summary>The dispatch started first (the point of no return): the cancellation is refused.</summary>
    DispatchStarted,

    /// <summary>The order is terminal for another reason (Completed, Rejected, Compensated).</summary>
    OrderTerminal,

    NotFound,
}

/// <summary>
/// Customer cancellation (I15): recorded only while the dispatch has not started, in one OrderDb transaction under the
/// order's row lock, the same lock BeginDispatch takes, and the database refuses an order both cancelled and
/// dispatched. Cancellation is cooperative: the fulfillment observes it when it begins, when its review wakes up (a
/// pending signal is queued for a waiting review), and at the point of no return.
/// </summary>
public sealed class CancellationSteps(string connectionString)
{
    public async Task<CancelOutcome> RequestAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        string status;
        bool requested, dispatched;
        await using (var read = new SqlCommand(
                         "SELECT Status, CASE WHEN CancelRequestedAt IS NULL THEN 0 ELSE 1 END, CASE WHEN DispatchStartedAt IS NULL THEN 0 ELSE 1 END FROM orders.[Order] WITH (UPDLOCK, ROWLOCK) WHERE OrderId = @o",
                         connection, tx))
        {
            read.Parameters.AddWithValue("@o", orderId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return CancelOutcome.NotFound;
            }

            (status, requested, dispatched) = (reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt32(2) == 1);
        }

        var outcome = status == nameof(OrderStatus.Cancelled) || requested ? CancelOutcome.AlreadyRequested
            : dispatched ? CancelOutcome.DispatchStarted
            : OrderStatuses.IsTerminal(Enum.Parse<OrderStatus>(status)) ? CancelOutcome.OrderTerminal
            : CancelOutcome.Recorded;
        if (outcome == CancelOutcome.Recorded)
        {
            await using var record = new SqlCommand(
                """
                UPDATE orders.[Order] SET CancelRequestedAt = SYSUTCDATETIME() WHERE OrderId = @o AND CancelRequestedAt IS NULL AND DispatchStartedAt IS NULL;
                -- A review that waits is woken (the workflow reads the cancellation from SQL).
                INSERT orders.PendingSignal (OrderId, InstanceId, EventName, CausedBy, State, Attempts)
                SELECT r.OrderId, @i, 'ReviewSignal_' + LOWER(REPLACE(CONVERT(varchar(36), r.ReviewId), '-', '')), @o, 'Pending', 0
                FROM orders.ReviewTask r
                WHERE r.OrderId = @o AND r.Status = 'Pending'
                  AND NOT EXISTS (SELECT 1 FROM orders.PendingSignal s WHERE s.CausedBy = @o AND s.EventName = 'ReviewSignal_' + LOWER(REPLACE(CONVERT(varchar(36), r.ReviewId), '-', '')));
                """, connection, tx);
            record.Parameters.AddWithValue("@o", orderId);
            record.Parameters.AddWithValue("@i", Fulfillment.Contracts.Workflows.FulfillmentWorkflow.InstanceId(orderId));
            await record.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Ends an order whose cancellation was recorded, from a status before reservation (Validating or AwaitingApproval):
    /// Cancelled with its history row, a pending review round Cancelled, the start request closed. Called inside the
    /// caller's transaction with the order locked. Returns true when it cancelled.
    /// </summary>
    internal static async Task<bool> CancelIfRequestedAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, OrderStatus status, StepCheckpoint checkpoint, CancellationToken ct)
    {
        await using (var check = new SqlCommand("SELECT COUNT(*) FROM orders.[Order] WHERE OrderId = @o AND CancelRequestedAt IS NOT NULL", connection, tx))
        {
            check.Parameters.AddWithValue("@o", orderId);
            if ((int)(await check.ExecuteScalarAsync(ct))! == 0)
            {
                return false;
            }
        }

        var key = $"{orderId:N}:cancel:1";
        if (!await OrderTransitions.TryTransitionAsync(connection, tx, orderId, status, OrderStatus.Cancelled, OrderReasonCodes.CancellationRequested, checkpoint, key, ct))
        {
            return false;
        }

        await using (var reviews = new SqlCommand($"UPDATE orders.ReviewTask SET Status = '{ReviewStates.Cancelled}' WHERE OrderId = @o AND Status = 'Pending'", connection, tx))
        {
            reviews.Parameters.AddWithValue("@o", orderId);
            await reviews.ExecuteNonQueryAsync(ct);
        }

        await OrderTransitions.CloseStartRequestAsync(connection, tx, orderId, ct);
        return true;
    }
}
