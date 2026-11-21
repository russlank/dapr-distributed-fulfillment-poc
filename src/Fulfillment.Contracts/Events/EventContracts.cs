using System.Text.Json;

namespace Fulfillment.Contracts.Events;

/// <summary>
/// A business event as published: its identity (<see cref="EventId"/>), its stream (<see cref="Source"/>,
/// <see cref="OrderId"/>) and position in it (<see cref="Sequence"/>, gapless from 1 per order and source),
/// and the source's data. Publication is at least once; consumers apply an event once per identity.
/// </summary>
public sealed record EventEnvelope(
    Guid EventId,
    string Source,
    Guid OrderId,
    int Sequence,
    string Type,
    int SchemaVersion,
    DateTime OccurredAtUtc,
    JsonElement Data);

/// <summary>The four event sources (one per owning database) and their topics.</summary>
public static class EventSources
{
    public const string Orders = "orders";
    public const string Inventory = "inventory";
    public const string Payments = "payments";
    public const string Shipping = "shipping";

    public const string PubSub = "pubsub";
    public const string DeadLetterTopic = "fulfillment.deadletter.v1";

    public static IReadOnlyList<string> All { get; } = [Orders, Inventory, Payments, Shipping];

    public static string TopicOf(string source) => $"fulfillment.{source}.v1";

    public static bool IsKnown(string? source) => source is Orders or Inventory or Payments or Shipping;
}

/// <summary>Event types, each owned by exactly one source.</summary>
public static class EventTypes
{
    public const int CurrentSchemaVersion = 1;

    public const string OrderStatusChanged = "OrderStatusChanged";
    public const string OrderCompleted = "OrderCompleted";
    public const string HoldCreated = "HoldCreated";
    public const string HoldReleased = "HoldReleased";
    public const string HoldExpired = "HoldExpired";
    public const string HoldConsumed = "HoldConsumed";
    public const string HoldExtended = "HoldExtended";
    public const string PaymentOperationRecorded = "PaymentOperationRecorded";
    public const string ShipmentDispatched = "ShipmentDispatched";
    public const string CarrierStatusRecorded = "CarrierStatusRecorded";

    private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal)
    {
        [OrderStatusChanged] = EventSources.Orders,
        [OrderCompleted] = EventSources.Orders,
        [HoldCreated] = EventSources.Inventory,
        [HoldReleased] = EventSources.Inventory,
        [HoldExpired] = EventSources.Inventory,
        [HoldConsumed] = EventSources.Inventory,
        [HoldExtended] = EventSources.Inventory,
        [PaymentOperationRecorded] = EventSources.Payments,
        [ShipmentDispatched] = EventSources.Shipping,
        [CarrierStatusRecorded] = EventSources.Shipping,
    };

    /// <summary>The source that owns <paramref name="type"/>, or null for an unknown type.</summary>
    public static string? SourceOf(string? type) => type is not null && Owners.TryGetValue(type, out var source) ? source : null;
}

/// <summary>Replay request for a source's outbox: rows of the order from <see cref="FromSequence"/> are published again.</summary>
public sealed record OutboxReplayRequest(Guid OrderId, int FromSequence);

public sealed record OutboxReplayResult(string Source, Guid OrderId, int FromSequence, int Replayed, int MaxSequence);

/// <summary>The highest published sequence of an order in a source's outbox.</summary>
public sealed record OutboxHead(Guid OrderId, int MaxPublishedSequence);

public static class OutboxRoutes
{
    public const string Replay = "internal/outbox/replay";
    public const string Heads = "internal/outbox/heads";
}
