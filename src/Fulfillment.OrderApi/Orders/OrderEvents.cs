using System.Text.Json;
using Fulfillment.Contracts.Events;
using Fulfillment.OrderApi.Domain;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Orders;

/// <summary>
/// The orders source's events: every status-history row gets one event with the row's sequence, in the same
/// transaction. The first transition to Completed is <c>OrderCompleted</c>; every other row is
/// <c>OrderStatusChanged</c> (including a post-terminal case recorded on a completed order).
/// </summary>
public static class OrderEvents
{
    public static string Payload(OrderStatus? from, OrderStatus to, string reasonCode) =>
        JsonSerializer.Serialize(new { fromStatus = from?.ToString(), toStatus = to.ToString(), reasonCode });

    public static string TypeOf(OrderStatus? from, OrderStatus to) =>
        to == OrderStatus.Completed && from != OrderStatus.Completed ? EventTypes.OrderCompleted : EventTypes.OrderStatusChanged;

    /// <summary>Appends the order's next status-history row and its event; the caller holds the order's lock and transaction.</summary>
    public static async Task AppendHistoryAndEventAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, OrderStatus from, OrderStatus to, string reasonCode,
        CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            DECLARE @seq int = (SELECT ISNULL(MAX(Sequence), 0) + 1 FROM orders.OrderStatusHistory WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @o);
            INSERT orders.OrderStatusHistory (OrderId, Sequence, FromStatus, ToStatus, ReasonCode) VALUES (@o, @seq, @from, @to, @reason);
            INSERT orders.OrderOutbox (EventId, OrderId, Sequence, EventType, Payload) VALUES (@id, @o, @seq, @type, @payload);
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@from", from.ToString());
        command.Parameters.AddWithValue("@to", to.ToString());
        command.Parameters.AddWithValue("@reason", reasonCode);
        command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@type", TypeOf(from, to));
        command.Parameters.AddWithValue("@payload", Payload(from, to, reasonCode));
        await command.ExecuteNonQueryAsync(ct);
    }
}
