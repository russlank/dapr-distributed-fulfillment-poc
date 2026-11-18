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

/// <summary>Outcomes of a fulfillment run.</summary>
public static class FulfillmentOutcomes
{
    /// <summary>The order was already terminal: the run ended without any effect.</summary>
    public const string Fenced = "AlreadyTerminal";

    /// <summary>
    /// The minimal workflow shell ended after a successful claim. The fulfillment steps (validation,
    /// checks, review, stub fulfillment) replace this outcome in the next implementation step.
    /// </summary>
    public const string ShellClaimed = "ShellClaimed";
}

public sealed record FulfillmentResult(string Outcome, int ExecutionClaims);
