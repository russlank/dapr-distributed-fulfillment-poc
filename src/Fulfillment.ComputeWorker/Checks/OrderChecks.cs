using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Fulfillment.ComputeWorker.Checks;

public sealed class ComputeCheckOptions
{
    public const string Section = "ComputeChecks";

    /// <summary>
    /// Simulated computation time of each check. The checks are independent and run concurrently, so
    /// the order's check phase takes about one of these, not the sum (scenario S02 measures it).
    /// </summary>
    public int SimulatedWorkMilliseconds { get; set; } = 300;

    /// <summary>
    /// Order value (minor units, stub price list) from which a human review is required: 1,000.00 by
    /// default (owner decision). Unset (null) means no order requires a review.
    /// </summary>
    public long? ReviewAmountThresholdMinor { get; set; } = 100_000;
}

/// <summary>
/// Stub price list: a fixed, deterministic unit price per SKU (no catalog exists yet). The version
/// is stored with each quote, so a later real price list is distinguishable from this one.
/// </summary>
public static class StubPriceList
{
    public const string Version = "stub-v1";
    public const string Currency = "EUR";

    /// <summary>100-9999 minor units, from a stable hash of the SKU (FNV-1a; never string.GetHashCode).</summary>
    public static long UnitPriceMinor(string sku)
    {
        ArgumentNullException.ThrowIfNull(sku);
        var hash = 2166136261u;
        foreach (var c in sku)
        {
            hash = unchecked((hash ^ c) * 16777619u);
        }

        return 100 + (hash % 9900);
    }

    public static long TotalMinor(IEnumerable<ValidatedLine> lines) => lines.Sum(l => UnitPriceMinor(l.Sku) * l.Quantity);
}

/// <summary>
/// Stub risk rule: a score from the order's size; a review is required when the order's value (stub
/// price list) reaches the configured threshold. The version is stored with each result.
/// </summary>
public static class StubRiskRule
{
    public const string Version = "stub-v2";

    public static int Score(IReadOnlyCollection<ValidatedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var units = lines.Sum(l => l.Quantity);
        return Math.Min(100, (lines.Count * 5) + (units / 10));
    }

    public static bool ReviewRequired(long valueMinor, long? thresholdMinor) => thresholdMinor is { } t && valueMinor >= t;
}

/// <summary>Pricing check (stateless; no I/O besides the simulated computation time).</summary>
public sealed class PriceOrderActivity(IOptions<ComputeCheckOptions> options, RuntimeIdentity identity) : WorkflowActivity<OrderCheckInput, PriceQuote>
{
    public override async Task<PriceQuote> RunAsync(WorkflowActivityContext context, OrderCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var started = DateTime.UtcNow;
        await Task.Delay(options.Value.SimulatedWorkMilliseconds);
        var total = StubPriceList.TotalMinor(input.Lines);
        return new PriceQuote(total, StubPriceList.Currency, StubPriceList.Version, new CheckExecution(identity.Replica, started, DateTime.UtcNow));
    }
}

/// <summary>Risk check (stateless; no I/O besides the simulated computation time).</summary>
public sealed class AssessOrderRiskActivity(IOptions<ComputeCheckOptions> options, RuntimeIdentity identity) : WorkflowActivity<OrderCheckInput, RiskAssessment>
{
    public override async Task<RiskAssessment> RunAsync(WorkflowActivityContext context, OrderCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var started = DateTime.UtcNow;
        await Task.Delay(options.Value.SimulatedWorkMilliseconds);
        var score = StubRiskRule.Score(input.Lines.ToList());
        var review = StubRiskRule.ReviewRequired(StubPriceList.TotalMinor(input.Lines), options.Value.ReviewAmountThresholdMinor);
        return new RiskAssessment(score, review, StubRiskRule.Version,
            new CheckExecution(identity.Replica, started, DateTime.UtcNow));
    }
}

