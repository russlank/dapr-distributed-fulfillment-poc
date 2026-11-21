using System.Globalization;
using System.Text.Json;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Events;
using Fulfillment.Messaging;
using Fulfillment.Messaging.Outbox;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Events;

public sealed record TimelineSource(string Source, int LastAppliedSequence, int PendingEvents, IReadOnlyList<int> MissingSequences, IReadOnlyDictionary<string, string> Fields);

public sealed record Timeline(Guid OrderId, string Stage, IReadOnlyList<TimelineSource> Sources, int DeadLetters);

/// <summary>The order's projection as read: per-source rows, their lag (pending events, missing sequences), and the combined stage.</summary>
public sealed class TimelineReader(string connectionString)
{
    public async Task<Timeline> ReadAsync(Guid orderId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var sources = new List<TimelineSource>();
        await using (var command = new SqlCommand(
                         """
                         SELECT s.Source, s.LastAppliedSequence, s.Fields,
                                (SELECT COUNT(*) FROM orders.OrderEventHistory h WHERE h.OrderId = s.OrderId AND h.Source = s.Source AND h.Disposition = 'Pending'),
                                (SELECT STRING_AGG(CAST(g.MissingSequence AS varchar(10)), ',') WITHIN GROUP (ORDER BY g.MissingSequence)
                                 FROM orders.OrderEventGaps g WHERE g.OrderId = s.OrderId AND g.Source = s.Source)
                         FROM orders.OrderTimelineState s WHERE s.OrderId = @o ORDER BY s.Source;
                         """, connection))
        {
            command.Parameters.AddWithValue("@o", orderId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var missing = reader.IsDBNull(4) ? [] : reader.GetString(4).Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
                sources.Add(new TimelineSource(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(3), missing, TimelineProjection.Deserialize(reader.GetString(2))));
            }
        }

        await using var dead = new SqlCommand("SELECT COUNT(*) FROM orders.EventDeadLetter WHERE OrderId = @o", connection);
        dead.Parameters.AddWithValue("@o", orderId);
        var deadLetters = (int)(await dead.ExecuteScalarAsync(ct))!;
        var stage = TimelineProjection.CombinedStage(sources.ToDictionary(s => s.Source, s => s.Fields));
        return new Timeline(orderId, stage, sources, deadLetters);
    }
}

/// <summary>
/// Outbox replay and heads for any source: the orders source through its own store, the others through
/// service invocation of their owning host (which answers only the order API).
/// </summary>
public sealed class OutboxReplayClient(OutboxStore orders, SidecarInvoker invoker)
{
    public static string AppIdOf(string source) => source switch
    {
        EventSources.Inventory => AppIds.InventoryService,
        EventSources.Payments => AppIds.PaymentService,
        EventSources.Shipping => AppIds.ShippingService,
        _ => AppIds.OrderApi,
    };

    public async Task<OutboxReplayResult?> ReplayAsync(string source, OutboxReplayRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (source == EventSources.Orders)
        {
            return await orders.ReplayAsync(request.OrderId, request.FromSequence, ct);
        }

        if (!EventSources.IsKnown(source))
        {
            return null;
        }

        var result = await invoker.PostAsync(AppIdOf(source), OutboxRoutes.Replay, request, $"replay:{source}:{request.OrderId:D}:{request.FromSequence}", ct);
        return result.IsSuccess ? JsonSerializer.Deserialize<OutboxReplayResult>(result.Body, DaprEventPublisher.Json) : null;
    }

    public async Task<IReadOnlyList<OutboxHead>?> HeadsAsync(string source, DateTime sinceUtc, CancellationToken ct)
    {
        if (source == EventSources.Orders)
        {
            return await orders.HeadsAsync(sinceUtc, ct);
        }

        var result = await invoker.GetAsync(AppIdOf(source), $"{OutboxRoutes.Heads}?since={Uri.EscapeDataString(sinceUtc.ToString("O", CultureInfo.InvariantCulture))}", ct);
        return result.IsSuccess ? JsonSerializer.Deserialize<List<OutboxHead>>(result.Body, DaprEventPublisher.Json) : null;
    }
}
