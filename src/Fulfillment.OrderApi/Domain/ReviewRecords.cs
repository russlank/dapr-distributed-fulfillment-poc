namespace Fulfillment.OrderApi.Domain;

/// <summary>
/// One review round of an order: the authority for its decision. Unique per (order, purpose, round);
/// the deadline is SQL time. A decision moves it Pending → Approved | Rejected only before the
/// deadline (by SQL time); only the expiry activity moves it Pending → Expired, at or after it.
/// </summary>
public sealed class ReviewTask
{
    private ReviewTask()
    {
        Purpose = null!;
        Status = null!;
    }

    public Guid ReviewId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Purpose { get; private set; }

    public int Round { get; private set; }

    /// <summary>Pending, Approved, Rejected, or Expired.</summary>
    public string Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime DeadlineAt { get; private set; }

    public DateTime? DecidedAt { get; private set; }

    /// <summary>The accepted decision (Approved or Rejected).</summary>
    public Guid? DecisionId { get; private set; }

    public DateTime? ExpiredAt { get; private set; }
}

/// <summary>
/// The accepted decision of a review: immutable (the database refuses updates), at most one per
/// review. The caller's decision id and the hash of its payload make retries recognisable.
/// </summary>
public sealed class ReviewDecision
{
    private ReviewDecision()
    {
        Outcome = null!;
        OperatorId = null!;
        PayloadHash = null!;
    }

    public Guid DecisionId { get; private set; }

    public Guid ReviewId { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Approved or Rejected.</summary>
    public string Outcome { get; private set; }

    public string OperatorId { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>SHA-256 of the normalized decision payload (32 bytes).</summary>
    public byte[] PayloadHash { get; private set; }

    public DateTime DecidedAt { get; private set; }
}

/// <summary>
/// A wake-up hint for a waiting workflow, written in the transaction of the fact it announces (the
/// fact itself stays in SQL). The signal dispatcher delivers it through the workflow host and
/// records the result: Forwarded only when the workflow host raised it to a running instance.
/// </summary>
public sealed class PendingSignal
{
    private PendingSignal()
    {
        InstanceId = null!;
        EventName = null!;
        State = null!;
    }

    public Guid SignalId { get; private set; }

    public Guid OrderId { get; private set; }

    public string InstanceId { get; private set; }

    public string EventName { get; private set; }

    /// <summary>The fact that caused the signal (for a review signal: the decision id).</summary>
    public Guid CausedBy { get; private set; }

    /// <summary>Pending, Forwarded, Discarded, or Stuck.</summary>
    public string State { get; private set; }

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAt { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public string? LastErrorCode { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? ForwardedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }
}

/// <summary>
/// Append-only record of one delivery attempt of a signal (written before the call, completed with
/// its result and the invocation's diagnostics).
/// </summary>
public sealed class SignalAttempt
{
    private SignalAttempt()
    {
        DispatcherId = null!;
    }

    public long Id { get; private set; }

    public Guid SignalId { get; private set; }

    public Guid LeaseToken { get; private set; }

    public string DispatcherId { get; private set; }

    public int Attempt { get; private set; }

    public DateTime CalledAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public string? Result { get; private set; }

    /// <summary>Runtime status the workflow host reported, if any.</summary>
    public string? ReportedStatus { get; private set; }

    /// <summary>Calls the invoker made (its bounded retries), the last HTTP status, and the last transport error.</summary>
    public int? InvocationAttempts { get; private set; }

    public int? HttpStatus { get; private set; }

    public string? TransportError { get; private set; }
}

/// <summary>One resolution of a review by its workflow (why it woke, and what SQL said): the run's review history.</summary>
public sealed class ReviewWake
{
    private ReviewWake()
    {
        Wake = null!;
        SeenStatus = null!;
    }

    public long Id { get; private set; }

    public Guid ReviewId { get; private set; }

    /// <summary>Initial, Signal, Timer, or Recheck.</summary>
    public string Wake { get; private set; }

    public string SeenStatus { get; private set; }

    public bool DeadlinePassed { get; private set; }

    public DateTime At { get; private set; }
}
