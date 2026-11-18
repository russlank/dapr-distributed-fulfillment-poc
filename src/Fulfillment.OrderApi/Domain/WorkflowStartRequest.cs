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

    /// <summary>Closed code of the last failed attempt (never a message or payload).</summary>
    public string? LastErrorCode { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    /// <summary>Execution claims accepted for this order (more than one after a replay or restart is normal).</summary>
    public int ExecutionClaims { get; private set; }

    /// <summary>Claims rejected because the order was already terminal.</summary>
    public int FenceRejections { get; private set; }

    public static WorkflowStartRequest ForNewOrder(Guid orderId) => new()
    {
        OrderId = orderId,
        InstanceId = FulfillmentWorkflow.InstanceId(orderId),
        State = WorkflowStartState.Pending,
    };
}
