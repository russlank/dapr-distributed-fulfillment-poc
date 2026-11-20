namespace Fulfillment.Contracts.Workflows;

/// <summary>Activities of the payment service (cross-app activities of the fulfillment and repair workflows).</summary>
public static class PaymentActivities
{
    /// <summary>Runs one payment operation against the provider by its operation key (authorize, capture, void, refund).</summary>
    public const string ExecutePayment = "ExecutePaymentOperation";

    /// <summary>
    /// Re-reads an operation finished Ambiguous from the provider (status queries with a fresh budget) and adopts a
    /// Committed or Prohibited answer; used after an intervention case was resolved. Never sends the operation.
    /// </summary>
    public const string ReconcilePayment = "ReconcilePaymentOperation";
}

/// <summary>Payment steps; the operation key is <c>{orderId:N}:{step}:{qualifier}</c>.</summary>
public static class PaymentSteps
{
    public const string Authorize = "authorize";
    public const string Capture = "capture";
    public const string Void = "void";
    public const string Refund = "refund";

    /// <summary>Qualifier of the original fulfillment's operations.</summary>
    public const string First = "1";

    /// <summary>Qualifier of an operation run by the repair workflow of an intervention case.</summary>
    public static string Repair(Guid caseId) => $"case-{caseId:N}";

    public static string Key(Guid orderId, string step, string qualifier) => $"{orderId:N}:{step}:{qualifier}";
}

/// <summary>
/// Outcomes of a payment operation, as the workflow sees them. Business results are outcomes, never
/// exceptions; only an infrastructure failure of the payment service itself throws.
/// </summary>
public static class PaymentOutcomes
{
    public const string Authorized = "Authorized";
    public const string Declined = "Declined";
    public const string Captured = "Captured";
    public const string Voided = "Voided";
    public const string Refunded = "Refunded";

    /// <summary>The compensated operation never committed (and now never will): nothing to void or refund.</summary>
    public const string NothingToCompensate = "NothingToCompensate";

    /// <summary>The provider refused it by rule (code: for example AuthorizationExpired, AlreadyCaptured).</summary>
    public const string Rejected = "Rejected";

    /// <summary>The operation's key was prohibited by an earlier compensation: it cannot commit.</summary>
    public const string Prohibited = "Prohibited";

    /// <summary>The provider could not be reached and no request can have reached it: nothing committed.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>A request may have reached the provider and its outcome could not be determined within the budget.</summary>
    public const string Ambiguous = "Ambiguous";
}

/// <param name="Step">authorize, capture, void, or refund (<see cref="PaymentSteps"/>).</param>
/// <param name="Qualifier">The key qualifier: "1" for the fulfillment, case-{id} for a repair.</param>
/// <param name="ReferenceStep">The step this one acts on: capture and void act on authorize, refund on capture.</param>
/// <param name="ReferenceQualifier">The qualifier of the referenced operation (normally "1").</param>
/// <param name="RetryUnresolved">An operation finished Unavailable or Ambiguous runs again with a fresh budget (same key, so
/// safe at the provider). Compensations use it; forward steps do not (their unresolved outcome goes to a case).</param>
public sealed record PaymentOperationRequest(
    Guid OrderId, string Step, string Qualifier, long AmountMinor, string Currency, string? ReferenceStep = null, string? ReferenceQualifier = null,
    bool RetryUnresolved = false);

/// <param name="Attempts">Requests sent to the provider by this and earlier executions of the operation.</param>
/// <param name="StatusQueries">Status queries made while reconciling.</param>
/// <param name="Replayed">The operation had already finished; this is its stored outcome.</param>
public sealed record PaymentOperationResult(
    string OperationKey,
    string Outcome,
    string? Code,
    DateTime? AuthorizationExpiresAtUtc,
    int Attempts,
    int StatusQueries,
    bool Replayed,
    string Replica);
