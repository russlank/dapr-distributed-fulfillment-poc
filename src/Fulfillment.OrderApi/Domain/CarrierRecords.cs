namespace Fulfillment.OrderApi.Domain;

/// <summary>A carrier callback accepted by the ingress (one row per valid unique event id; repeats are counted).</summary>
public sealed class CarrierCallbackInbox
{
    public long Id { get; private set; }

    public string Source { get; private set; } = "";

    public Guid EventId { get; private set; }

    public string PayloadHash { get; private set; } = "";

    public Guid ShipmentId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Status { get; private set; } = "";

    public int Version { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public DateTime ReceivedAt { get; private set; }

    public int Duplicates { get; private set; }
}

/// <summary>
/// The command to forward one accepted callback to Shipping; written in the ingress transaction, processed by the
/// leased forwarder, never dropped. Processed records Shipping's outcome.
/// </summary>
public sealed class CarrierStatusForward
{
    public Guid EventId { get; private set; }

    /// <summary>Pending or Processed.</summary>
    public string State { get; private set; } = "";

    public string? Outcome { get; private set; }

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAt { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public string? LastError { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }
}

/// <summary>Audit of a callback that reused an event id with another payload (refused with 409, or reported by Shipping).</summary>
public sealed class CarrierCallbackConflict
{
    public long Id { get; private set; }

    public Guid EventId { get; private set; }

    public string StoredHash { get; private set; } = "";

    public string ReceivedHash { get; private set; } = "";

    public string DetectedBy { get; private set; } = "";

    public DateTime DetectedAt { get; private set; }
}

/// <summary>
/// The order's events (source <c>orders</c>), written in the same transaction as the fact: one per status-history
/// row, with the history row's sequence (gapless per order). Published at least once by the relay.
/// </summary>
public sealed class OrderOutboxEntry
{
    public long Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid OrderId { get; private set; }

    public int Sequence { get; private set; }

    public string EventType { get; private set; } = "";

    public int SchemaVersion { get; private set; }

    public string Payload { get; private set; } = "";

    public string State { get; private set; } = "";

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseUntil { get; private set; }

    public int Generation { get; private set; }

    public bool Stalled { get; private set; }

    public string? LastError { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? PublishedAt { get; private set; }

    /// <summary>The event of an order's first status (acceptance), saved with the order.</summary>
    public static OrderOutboxEntry Accepted(Guid orderId) => new()
    {
        EventId = Guid.CreateVersion7(),
        OrderId = orderId,
        Sequence = 1,
        EventType = Fulfillment.Contracts.Events.EventTypes.OrderStatusChanged,
        SchemaVersion = 1,
        Payload = Orders.OrderEvents.Payload(null, OrderStatus.Submitted, OrderReasonCodes.Accepted),
        State = "Pending",
    };
}
