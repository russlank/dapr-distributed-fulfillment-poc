namespace Fulfillment.OrderApi.Domain;

/// <summary>
/// The consumer's inbox and event history: every valid event once per event id, whatever its arrival order
/// (<c>Pending</c> until it is the next sequence of its stream, then <c>Applied</c>). The model exists for
/// migrations; the consumer runs raw SQL.
/// </summary>
public sealed class OrderEventHistory
{
    public Guid EventId { get; private set; }

    public string Source { get; private set; } = "";

    public Guid OrderId { get; private set; }

    public int Sequence { get; private set; }

    public string Type { get; private set; } = "";

    public int SchemaVersion { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public DateTime ReceivedAt { get; private set; }

    public string PayloadHash { get; private set; } = "";

    public string Payload { get; private set; } = "";

    public string Disposition { get; private set; } = "";

    public DateTime? AppliedAt { get; private set; }

    /// <summary>Deliveries of this event after the first (republished or redelivered), each acknowledged without change.</summary>
    public int Duplicates { get; private set; }
}

/// <summary>Current state per order and source: only the fields that source owns, at its last contiguous sequence.</summary>
public sealed class OrderTimelineState
{
    public Guid OrderId { get; private set; }

    public string Source { get; private set; } = "";

    public int LastAppliedSequence { get; private set; }

    public string Fields { get; private set; } = "";

    public DateTime UpdatedAt { get; private set; }
}

/// <summary>Dead-letter recorder: ids and reason codes only, never payloads.</summary>
public sealed class EventDeadLetter
{
    public long Id { get; private set; }

    public Guid? EventId { get; private set; }

    public string? MessageId { get; private set; }

    public string? Source { get; private set; }

    public Guid? OrderId { get; private set; }

    public int? Sequence { get; private set; }

    public string? Type { get; private set; }

    public string Reason { get; private set; } = "";

    public string? Topic { get; private set; }

    public DateTime RecordedAt { get; private set; }
}

/// <summary>The reconciler's lease: one instance across the order API replicas runs a pass at a time.</summary>
public sealed class ReconcilerLease
{
    public string Name { get; private set; } = "";

    public string? Owner { get; private set; }

    public DateTime? LeaseUntil { get; private set; }

    public DateTime? LastRunAt { get; private set; }
}
