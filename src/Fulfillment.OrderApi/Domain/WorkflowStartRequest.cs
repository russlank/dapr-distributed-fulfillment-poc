using Fulfillment.Contracts.Workflows;

namespace Fulfillment.OrderApi.Domain;

/// <summary>Lifecycle of a start request: Pending until a workflow exists, Started, then Closed when the order is terminal.</summary>
public enum WorkflowStartState
{
    Pending,
    Started,
    Closed,
}

/// <summary>
/// Workflow execution state of one order, kept apart from the business state. Exactly one per order
/// (the order id is the key), created in the acceptance transaction, so an accepted order always has
/// a durable start intent. The start dispatcher claims due Pending rows with a lease, starts the
/// instance through the workflow host's management API, and marks the row Started; the execution
/// claim counts claims and fence rejections here. Nothing in this row is business truth.
/// </summary>
public sealed class WorkflowStartRequest
{
    private WorkflowStartRequest()
    {
        InstanceId = null!;
    }

    public Guid OrderId { get; private set; }

    /// <summary>Stable workflow instance id, derived from the order id (one instance per order).</summary>
    public string InstanceId { get; private set; }

    public WorkflowStartState State { get; private set; }

    /// <summary>Database time from which the dispatcher may claim the request (backoff after failures).</summary>
    public DateTime NextAttemptAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Replica holding the dispatch lease, if any.</summary>
    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAt { get; private set; }

    /// <summary>
    /// Fencing token of the current claim, new on every claim. Every update after the claim matches
    /// it, so a dispatcher whose lease expired and was claimed again cannot change the row.
    /// </summary>
    public Guid? LeaseToken { get; private set; }

    /// <summary>Closed code of the last failed attempt (never a message or payload).</summary>
    public string? LastErrorCode { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    /// <summary>Execution claims accepted for this order (more than one after a replay or restart is normal).</summary>
    public int ExecutionClaims { get; private set; }

    /// <summary>Claims rejected because the order was already terminal.</summary>
    public int FenceRejections { get; private set; }

    /// <summary>Workflow version executing the order, recorded by runs with patch execution-version-2; null for earlier runs.</summary>
    public int? ExecutionVersion { get; private set; }

    public static WorkflowStartRequest ForNewOrder(Guid orderId) => new()
    {
        OrderId = orderId,
        InstanceId = FulfillmentWorkflow.InstanceId(orderId),
        State = WorkflowStartState.Pending,
    };
}

/// <summary>
/// Append-only record of one start attempt: written before the call to the workflow host and
/// completed with its result. Overlapping attempts for one order show concurrent starts; a
/// <c>LeaseLost</c> result shows a stale dispatcher that was kept from changing the request.
/// </summary>
public sealed class WorkflowStartAttempt
{
    private WorkflowStartAttempt()
    {
        DispatcherId = null!;
        InstanceId = null!;
    }

    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid LeaseToken { get; private set; }

    public string DispatcherId { get; private set; }

    public string InstanceId { get; private set; }

    public int Attempt { get; private set; }

    public DateTime CalledAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>Started, AlreadyExists, Ambiguous, Rejected, or LeaseLost (closed code domain).</summary>
    public string? Result { get; private set; }

    /// <summary>Runtime status the workflow host reported, if any.</summary>
    public string? ReportedStatus { get; private set; }
}

/// <summary>One row per execution claim made by a workflow run (audit of the execution fence).</summary>
public sealed class ExecutionClaim
{
    private ExecutionClaim()
    {
        InstanceId = null!;
        Outcome = null!;
        OrderStatus = null!;
    }

    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string InstanceId { get; private set; }

    /// <summary>Proceed or AlreadyTerminal.</summary>
    public string Outcome { get; private set; }

    /// <summary>Business status the claim saw.</summary>
    public string OrderStatus { get; private set; }

    public DateTime ClaimedAt { get; private set; }
}
