namespace Fulfillment.Contracts.Workflows;

/// <summary>
/// Identity of the fulfillment workflow. The instance id is derived from the order id alone (no
/// version suffix), so every component computes the same id and an order has exactly one.
/// </summary>
public static class FulfillmentWorkflow
{
    public const string Name = "FulfillOrderWorkflow";

    public const string InstancePrefix = "fulfill-";

    /// <summary>First activity of every run (on order-api): the SQL execution fence.</summary>
    public const string ClaimExecutionActivity = "ClaimFulfillmentExecution";

    /// <summary>On order-api: Submitted → Validating, business rules, rejection of an invalid order.</summary>
    public const string ValidateOrderActivity = "ValidateFulfillmentOrder";

    /// <summary>On compute-worker: price of the order (stub price list, no I/O).</summary>
    public const string PriceOrderActivity = "PriceOrder";

    /// <summary>On compute-worker: risk score and whether a human review is required.</summary>
    public const string AssessOrderRiskActivity = "AssessOrderRisk";

    /// <summary>On order-api: stores the check results once per check (keyed).</summary>
    public const string RecordChecksActivity = "RecordFulfillmentChecks";

    /// <summary>On order-api: one simulated fulfillment step, recorded once per operation key.</summary>
    public const string ApplyStubStepActivity = "ApplyStubFulfillmentStep";

    /// <summary>On order-api: the terminal transaction (stub completion effect, Completed, start request Closed).</summary>
    public const string CompleteStubFulfillmentActivity = "CompleteStubFulfillment";

    public static string InstanceId(Guid orderId) => InstancePrefix + orderId.ToString("N");
}

/// <summary>Input of a fulfillment run: only the order id; everything else is read from SQL by activities.</summary>
public sealed record FulfillmentInput(Guid OrderId);

/// <summary>Outcomes of the execution claim (closed code domain).</summary>
public static class ExecutionClaimOutcomes
{
    public const string Proceed = "Proceed";
    public const string AlreadyTerminal = "AlreadyTerminal";
}

/// <summary>Claim input: the order and the instance claiming it (for the audit row).</summary>
public sealed record ExecutionClaimRequest(Guid OrderId, string InstanceId);

/// <summary>Claim result: whether the run may continue, and the claim count after this claim.</summary>
public sealed record ExecutionClaimResult(string Outcome, string OrderStatus, int ExecutionClaims)
{
    public bool Proceed => Outcome == ExecutionClaimOutcomes.Proceed;
}

/// <summary>Outcomes of the order-api steps of a run (closed code domain).</summary>
public static class StepOutcomes
{
    /// <summary>The step's effect was committed by this execution.</summary>
    public const string Applied = "Applied";

    /// <summary>The effect had been committed before (a retried or repeated execution); its stored result is returned.</summary>
    public const string AlreadyApplied = "AlreadyApplied";

    /// <summary>The order is terminal: nothing was changed and the run must end.</summary>
    public const string AlreadyTerminal = "AlreadyTerminal";

    /// <summary>Validation: the order is valid and Validating.</summary>
    public const string Valid = "Valid";

    /// <summary>Validation: the order broke a business rule and is now Rejected.</summary>
    public const string Rejected = "Rejected";
}

public sealed record ValidateOrderRequest(Guid OrderId, string InstanceId);

/// <summary>A line as validated; the checks and the stub steps work from this snapshot (workflow history), not from SQL.</summary>
public sealed record ValidatedLine(int LineNumber, string Sku, int Quantity);

/// <param name="Outcome">Valid, Rejected, or AlreadyTerminal.</param>
/// <param name="RuleCode">The broken business rule when Rejected.</param>
public sealed record OrderValidationResult(string Outcome, string OrderStatus, string? RuleCode, string CustomerId, IReadOnlyList<ValidatedLine> Lines);

/// <summary>Input of every compute check: the validated order, nothing read from a database.</summary>
public sealed record OrderCheckInput(Guid OrderId, string CustomerId, IReadOnlyList<ValidatedLine> Lines);

/// <summary>
/// Where and when a compute check ran (wall clock of the compute replica, recorded as evidence that
/// independent checks run concurrently; never used for a business decision).
/// </summary>
public sealed record CheckExecution(string Replica, DateTime StartedAtUtc, DateTime CompletedAtUtc);

/// <param name="TotalMinor">Order total in minor units (stub price list).</param>
public sealed record PriceQuote(long TotalMinor, string Currency, string PriceListVersion, CheckExecution Execution);

/// <param name="Score">0-100; higher is riskier.</param>
/// <param name="ReviewRequired">True when the configured review threshold is reached.</param>
public sealed record RiskAssessment(int Score, bool ReviewRequired, string RuleVersion, CheckExecution Execution);

public sealed record RecordChecksRequest(Guid OrderId, PriceQuote Price, RiskAssessment Risk);

/// <summary>Check names and their operation-key qualifiers (one stored result per check).</summary>
public static class FulfillmentChecks
{
    public const string Step = "check";
    public const string Price = "price";
    public const string Risk = "risk";
}

/// <summary>
/// Simulated fulfillment steps of M1. They stand in for reservation, payment, and shipment until
/// those exist, and they are recognisable as such everywhere: the step names start with "stub-",
/// the rows live in their own table, and no other service is called.
/// </summary>
public static class StubSteps
{
    public const string Prefix = "stub-";

    /// <summary>One simulated pick per order line (qualifier line-N), in line order.</summary>
    public const string Fulfil = "stub-fulfil";

    /// <summary>The completion of the simulated fulfillment (qualifier 1), in the terminal transaction.</summary>
    public const string Complete = "stub-complete";

    public static string LineQualifier(int lineNumber) => $"line-{lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

public sealed record StubStepRequest(Guid OrderId, string Step, string Qualifier);

/// <summary>Result of an order-api step that records an effect.</summary>
/// <param name="Outcome">Applied, AlreadyApplied, or AlreadyTerminal.</param>
/// <param name="OperationKey">The step's key; null when it has several (checks) or none was looked up (terminal order).</param>
public sealed record StepResult(string Outcome, string? OperationKey, string OrderStatus)
{
    public bool Terminal => Outcome == StepOutcomes.AlreadyTerminal;
}

public sealed record CompleteFulfillmentRequest(Guid OrderId, string InstanceId);

/// <summary>Outcomes of a fulfillment run.</summary>
public static class FulfillmentOutcomes
{
    /// <summary>The order was already terminal: the run ended without any effect.</summary>
    public const string Fenced = "AlreadyTerminal";

    /// <summary>The order broke a business rule and was rejected.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The simulated fulfillment completed the order.</summary>
    public const string Completed = "Completed";
}

public sealed record FulfillmentResult(string Outcome, string OrderStatus, int ExecutionClaims);
