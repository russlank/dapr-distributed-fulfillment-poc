using Fulfillment.Contracts.Events;
using Fulfillment.OrderApi.Events;
using Xunit;

namespace Fulfillment.UnitTests;

/// <summary>
/// S48 deterministic suite: the consumer's pure rules over every arrival order of a fixed event set, gaps held as
/// pending and drained when filled, conflicts dead-lettered, and a rebuild equal to live state.
/// </summary>
public class TimelineProjectionTests
{
    private static readonly Guid Order = Guid.Parse("01a1f9aa-0000-7000-8000-000000000001");
    private static readonly Guid H1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid H2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static IncomingEvent Inventory(int sequence, string type, Guid hold, Guid? eventId = null, string? extra = null)
    {
        var data = extra is null ? $"{{\"holdId\":\"{hold}\"}}" : $"{{\"holdId\":\"{hold}\",{extra}}}";
        return new IncomingEvent(eventId ?? Guid.Parse($"00000000-0000-0000-0000-{sequence:D12}"), EventSources.Inventory, Order, sequence, type,
            1, new DateTime(2026, 11, 21, 0, 0, sequence, DateTimeKind.Utc), data,
            IncomingEvent.HashOf(EventSources.Inventory, Order, sequence, type, 1, data));
    }

    /// <summary>Sequences 1..5, each a partial update of a different field, plus a duplicate of sequence 3.</summary>
    private static IncomingEvent[] Set() =>
    [
        Inventory(1, EventTypes.HoldCreated, H1),
        Inventory(2, EventTypes.HoldCreated, H2),
        Inventory(3, EventTypes.HoldExtended, H1, extra: "\"expiresAt\":\"2026-11-21T00:30:00Z\""),
        Inventory(4, EventTypes.HoldConsumed, H1),
        Inventory(5, EventTypes.HoldConsumed, H2),
        Inventory(3, EventTypes.HoldExtended, H1, extra: "\"expiresAt\":\"2026-11-21T00:30:00Z\""),
    ];

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    [Fact]
    public void Every_arrival_order_gives_the_same_state_history_and_checksum()
    {
        var events = Set();
        var control = new TimelineModel();
        foreach (var e in events)
        {
            control.Ingest(EventSources.Inventory, e);
        }

        var count = 0;
        foreach (var order in Permutations([0, 1, 2, 3, 4, 5]))
        {
            var model = new TimelineModel();
            foreach (var index in order)
            {
                model.Ingest(EventSources.Inventory, events[index]);
            }

            Assert.Equal(control.Checksum(), model.Checksum());
            Assert.Equal(5, model.LastApplied(EventSources.Inventory, Order));
            Assert.All(model.History.Values, d => Assert.Equal(Dispositions.Applied, d));
            Assert.Equal(5, model.History.Count);
            Assert.Equal(1, model.Duplicates.Values.Sum());
            Assert.Empty(model.DeadLetters);
            count++;
        }

        Assert.Equal(720, count);
        Assert.Equal("Consumed", control.Fields(EventSources.Inventory, Order)[$"hold:{H1}"]);
        Assert.Equal("Consumed", control.Fields(EventSources.Inventory, Order)[$"hold:{H2}"]);
    }

    [Fact]
    public void A_missing_sequence_keeps_later_events_pending_and_filling_it_drains_them_at_once()
    {
        var events = Set();
        var model = new TimelineModel();
        Assert.Equal(Dispositions.Applied, model.Ingest(EventSources.Inventory, events[0]));
        Assert.Equal(Dispositions.Pending, model.Ingest(EventSources.Inventory, events[2]));
        Assert.Equal(Dispositions.Pending, model.Ingest(EventSources.Inventory, events[3]));

        // State stays at the last contiguous sequence: the fields 3 and 4 would change are not applied.
        Assert.Equal(1, model.LastApplied(EventSources.Inventory, Order));
        var fields = model.Fields(EventSources.Inventory, Order);
        Assert.Equal("Held", fields[$"hold:{H1}"]);
        Assert.False(fields.ContainsKey($"holdExpiresAt:{H1}"));
        Assert.False(fields.ContainsKey($"hold:{H2}"));

        Assert.Equal(Dispositions.Applied, model.Ingest(EventSources.Inventory, events[1]));
        Assert.Equal(4, model.LastApplied(EventSources.Inventory, Order));
        Assert.Equal("Consumed", model.Fields(EventSources.Inventory, Order)[$"hold:{H1}"]);
    }

    [Fact]
    public void Payload_and_sequence_conflicts_are_dead_lettered_and_never_overwrite()
    {
        var model = new TimelineModel();
        var original = Inventory(1, EventTypes.HoldCreated, H1);
        model.Ingest(EventSources.Inventory, original);
        var checksum = model.Checksum();

        var samePayloadOtherId = Inventory(1, EventTypes.HoldCreated, H1, eventId: Guid.NewGuid());
        Assert.Equal(Dispositions.SequenceConflict, model.Ingest(EventSources.Inventory, samePayloadOtherId));

        var sameIdOtherPayload = Inventory(1, EventTypes.HoldReleased, H1, eventId: original.EventId);
        Assert.Equal(Dispositions.EventIdPayloadConflict, model.Ingest(EventSources.Inventory, sameIdOtherPayload));

        var behind = Inventory(1, EventTypes.HoldCreated, H2, eventId: Guid.NewGuid());
        Assert.Equal(Dispositions.SequenceConflict, model.Ingest(EventSources.Inventory, behind));

        Assert.Equal(checksum, model.Checksum());
        Assert.Equal(3, model.DeadLetters.Count);
    }

    [Fact]
    public void Wrong_source_unknown_schema_and_invalid_data_are_dead_lettered()
    {
        var model = new TimelineModel();
        var e = Inventory(1, EventTypes.HoldCreated, H1);
        Assert.Equal(Dispositions.SourceMismatch, model.Ingest(EventSources.Payments, e));
        Assert.Equal(Dispositions.SourceMismatch, model.Ingest(EventSources.Inventory, e with { Type = EventTypes.PaymentOperationRecorded }));
        Assert.Equal(Dispositions.UnknownSchemaVersion, model.Ingest(EventSources.Inventory, e with { SchemaVersion = 2 }));
        Assert.Equal(Dispositions.InvalidPayload, model.Ingest(EventSources.Inventory, e with { Data = "{}" }));
        Assert.Equal(Dispositions.InvalidPayload, model.Ingest(EventSources.Inventory, e with { Data = "not json" }));
        Assert.Equal(0, model.LastApplied(EventSources.Inventory, Order));
    }

    [Fact]
    public void A_rebuild_from_history_in_sequence_order_equals_live_state()
    {
        var events = Set();
        var live = new TimelineModel();
        foreach (var index in new[] { 4, 2, 5, 0, 3, 1 })
        {
            live.Ingest(EventSources.Inventory, events[index]);
        }

        var rebuilt = new TimelineModel();
        foreach (var e in events.Take(5).OrderBy(e => e.Sequence))
        {
            rebuilt.Ingest(EventSources.Inventory, e);
        }

        Assert.Equal(live.Checksum(), rebuilt.Checksum());
    }

    [Fact]
    public void The_combined_stage_is_computed_from_the_sources_when_read()
    {
        var orders = new Dictionary<string, string> { ["status"] = "AwaitingDelivery" };
        var shipping = new Dictionary<string, string> { ["carrierStatus"] = "InTransit" };
        Assert.Equal("Shipping:InTransit", TimelineProjection.CombinedStage(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [EventSources.Orders] = orders,
            [EventSources.Shipping] = shipping,
        }));
        Assert.Equal("Completed", TimelineProjection.CombinedStage(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [EventSources.Orders] = new Dictionary<string, string> { ["status"] = "Completed" },
            [EventSources.Shipping] = shipping,
        }));
    }
}
