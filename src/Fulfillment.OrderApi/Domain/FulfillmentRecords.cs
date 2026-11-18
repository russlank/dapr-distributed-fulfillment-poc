namespace Fulfillment.OrderApi.Domain;

/// <summary>
/// One simulated fulfillment effect of M1, keyed by its operation key. Simulated effects stand in
/// for reservation, payment, and shipment until those services do real work; they live in their own
/// table, their steps start with "stub-", and nothing outside OrderDb is touched. The key is the
/// primary key, so an effect exists at most once; a repeated execution of the step finds it and only
/// counts the replay.
/// </summary>
public sealed class StubEffect
{
    private StubEffect()
    {
        OperationKey = null!;
        Step = null!;
        Qualifier = null!;
        AppliedBy = null!;
    }

    public string OperationKey { get; private set; }

    public Guid OrderId { get; private set; }

    public string Step { get; private set; }

    public string Qualifier { get; private set; }

    /// <summary>Replica that committed the effect.</summary>
    public string AppliedBy { get; private set; }

    public DateTime AppliedAt { get; private set; }

    /// <summary>Executions that found the effect already committed (activity retries, repeated runs).</summary>
    public int Replays { get; private set; }
}

/// <summary>
/// Stored result of one compute check of an order (one row per check). The first committed result
/// is kept; later executions only count the replay. The run's timing columns come from the compute
/// replica's clock and are evidence of concurrent execution, not business data.
/// </summary>
public sealed class OrderCheck
{
    private OrderCheck()
    {
        CheckName = null!;
        OperationKey = null!;
        Replica = null!;
        RuleVersion = null!;
    }

    public Guid OrderId { get; private set; }

    /// <summary>price or risk.</summary>
    public string CheckName { get; private set; }

    public string OperationKey { get; private set; }

    public string Replica { get; private set; }

    public DateTime StartedAt { get; private set; }

    public DateTime CompletedAt { get; private set; }

    public long? TotalMinor { get; private set; }

    public string? Currency { get; private set; }

    public int? RiskScore { get; private set; }

    public bool? ReviewRequired { get; private set; }

    /// <summary>Version of the price list or risk rule that produced the result.</summary>
    public string RuleVersion { get; private set; }

    public DateTime RecordedAt { get; private set; }

    public int Replays { get; private set; }
}
