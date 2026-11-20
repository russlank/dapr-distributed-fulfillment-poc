namespace Fulfillment.Contracts.Workflows;

/// <summary>Order-api activities of the fulfillment after reservation (payment, packing, dispatch, interventions).</summary>
public static class OrderLifecycleActivities
{
    /// <summary>One forward status step of a running fulfillment (Reserving → PaymentAuthorized → Packing), conditional and idempotent.</summary>
    public const string AdvanceFulfillment = "AdvanceFulfillment";

    /// <summary>The cancellation point of no return: records the dispatch start unless a cancellation was requested first.</summary>
    public const string BeginDispatch = "BeginDispatch";

    /// <summary>Opens an intervention case (one open case per order and kind); a non-terminal order waits in ManualInterventionRequired.</summary>
    public const string OpenInterventionCase = "OpenInterventionCase";

    /// <summary>Reads a case from SQL after a wake-up (the workflow never trusts the signal's content).</summary>
    public const string ReadInterventionCase = "ReadInterventionCase";

    /// <summary>The terminal transaction of a delivered order (Completed, OrderCompleted outbox row, start request closed).</summary>
    public const string FinalizeDelivered = "FinalizeDelivered";
}

public sealed record AdvanceFulfillmentRequest(Guid OrderId, string ToStatus);

public static class DispatchOutcomes
{
    public const string Started = "Started";
    public const string AlreadyStarted = "AlreadyStarted";

    /// <summary>A cancellation was recorded first: dispatch must not start.</summary>
    public const string CancelRequested = "CancelRequested";

    public const string AlreadyTerminal = "AlreadyTerminal";
}

public sealed record BeginDispatchRequest(Guid OrderId);

public sealed record BeginDispatchResult(string Outcome, string OrderStatus);

/// <summary>Kinds of intervention cases (plan Section 7.4).</summary>
public static class InterventionKinds
{
    public const string AmbiguousPayment = "AmbiguousPayment";
    public const string AmbiguousDispatch = "AmbiguousDispatch";
    public const string CarrierTerminalConflict = "CarrierTerminalConflict";
    public const string DeliveryException = "DeliveryException";
    public const string DeliveryOverdue = "DeliveryOverdue";
    public const string CompensationExhausted = "CompensationExhausted";
}

public static class CaseStates
{
    public const string Open = "Open";
    public const string Reconciling = "Reconciling";
    public const string Resolved = "Resolved";
    public const string Abandoned = "Abandoned";
}

/// <summary>Resolution actions of an operator (POST /interventions/{id}/resolution).</summary>
public static class CaseActions
{
    /// <summary>The external effect happened (evidence matches the provider or carrier record): continue as if it had succeeded.</summary>
    public const string ConfirmExternalSuccess = "ConfirmExternalSuccess";

    /// <summary>The external effect did not happen: compensate.</summary>
    public const string ConfirmExternalFailure = "ConfirmExternalFailure";

    /// <summary>Informational case taken note of; nothing to do.</summary>
    public const string Acknowledge = "Acknowledge";
}

public static class InterventionSignals
{
    public static string EventName(Guid caseId) => $"InterventionResolved_{caseId:N}";
}

/// <param name="Qualifier">Distinguishes cases of one kind within one run (for example the payment step), part of the case's idempotency key.</param>
/// <param name="Evidence">What the workflow knew when it opened the case (operation key, attempts, last answers).</param>
public sealed record OpenCaseRequest(Guid OrderId, string Kind, string Qualifier, string Evidence);

public sealed record ReadCaseRequest(Guid CaseId);

/// <param name="Action">The resolution action once Resolved.</param>
/// <param name="Created">This execution opened the case (false: it existed already for this key).</param>
public sealed record CaseResult(Guid CaseId, string Kind, string State, string? Action, string OrderStatus, bool OrderTerminal, bool Created);
