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

/// <summary>Business events of the order written in the same transaction as the fact (write side; publication arrives later).</summary>
public sealed class OrderOutboxEntry
{
    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string EventType { get; private set; } = "";

    public string Payload { get; private set; } = "";

    public string State { get; private set; } = "";

    public DateTime CreatedAt { get; private set; }
}
