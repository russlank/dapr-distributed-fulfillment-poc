using Fulfillment.OrderApi.Domain;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Orders;

/// <summary>
/// Named points inside the fulfillment transactions. Tests use them to fail a transaction between
/// its statements (proving that it rolls back as a whole); drill builds map them to failpoints, for
/// example to end the process right after an effect committed. Production code passes a no-op.
/// </summary>
public delegate Task StepCheckpoint(string name, string operationKey, CancellationToken ct);

/// <summary>Checkpoint names (also the failpoint names of drill builds).</summary>
public static class StepCheckpoints
{
    public const string AfterStatusUpdate = "AfterOrderStatusUpdate";
    public const string AfterHistoryInsert = "AfterOrderStatusHistoryInsert";
    public const string AfterEffectInsert = "AfterStubEffectInsert";
    public const string AfterStartRequestClose = "AfterStartRequestClose";

    /// <summary>Before a stub step's transaction (drills hold it here with a delay).</summary>
    public const string BeforeStubStep = "BeforeStubFulfillmentStep";

    /// <summary>After a stub effect committed, before the activity returns (drills end the process here).</summary>
    public const string AfterStubEffectCommit = "AfterStubEffectCommitBeforeActivityCompletion";

    /// <summary>After the terminal transaction committed, before the activity returns.</summary>
    public const string AfterTerminalCommit = "AfterTerminalCommitBeforeActivityCompletion";

    public static readonly StepCheckpoint None = (_, _, _) => Task.CompletedTask;
}

/// <summary>
/// Conditional order-status transitions inside a caller's transaction. A transition changes the
/// order only if it is still in the expected status, and appends the matching history row in the
/// same transaction, numbered one after the highest existing number. The caller holds an update lock
/// on the order row from the start of its transaction, so transitions of one order are serialized
/// and the numbering cannot interleave; the unique (OrderId, Sequence) index is the backstop.
/// </summary>
public static class OrderTransitions
{
    private static readonly string TerminalList = string.Join(", ",
        Enum.GetValues<OrderStatus>().Where(OrderStatuses.IsTerminal).Select(s => $"'{s}'"));

    /// <summary>Reads the order's status with an update lock held to the end of the transaction; null when there is no order.</summary>
    public static async Task<(OrderStatus Status, string CustomerId)?> LockAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT Status, CustomerId FROM orders.[Order] WITH (UPDLOCK, ROWLOCK) WHERE OrderId = @o;", connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (Enum.Parse<OrderStatus>(reader.GetString(0)), reader.GetString(1)) : null;
    }

    /// <summary>
    /// Moves the order from <paramref name="from"/> to <paramref name="to"/> and appends the history
    /// row. False when the order is not in <paramref name="from"/> (nothing changed).
    /// </summary>
    public static async Task<bool> TryTransitionAsync(SqlConnection connection, SqlTransaction tx, Guid orderId,
        OrderStatus from, OrderStatus to, string reasonCode, StepCheckpoint checkpoint, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using (var update = new SqlCommand(
                         "UPDATE orders.[Order] SET Status = @to WHERE OrderId = @o AND Status = @from;", connection, tx))
        {
            update.Parameters.AddWithValue("@o", orderId);
            update.Parameters.AddWithValue("@from", from.ToString());
            update.Parameters.AddWithValue("@to", to.ToString());
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                return false;
            }
        }

        await checkpoint(StepCheckpoints.AfterStatusUpdate, operationKey, ct);
        await OrderEvents.AppendHistoryAndEventAsync(connection, tx, orderId, from, to, reasonCode, ct);

        await checkpoint(StepCheckpoints.AfterHistoryInsert, operationKey, ct);
        return true;
    }

    /// <summary>
    /// Closes the order's start request; part of every transaction that makes the order terminal.
    /// Any lease is cleared: a dispatcher still holding one finds the request closed afterwards.
    /// </summary>
    public static async Task CloseStartRequestAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            $"""
            UPDATE orders.WorkflowStartRequest
            SET State = 'Closed', ClosedAt = SYSUTCDATETIME(), LastErrorCode = NULL,
                LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
            WHERE OrderId = @o AND State <> 'Closed'
              AND EXISTS (SELECT 1 FROM orders.[Order] WHERE OrderId = @o AND Status IN ({TerminalList}));
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
