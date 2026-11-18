namespace Fulfillment.Contracts.Diagnostics;

// The runtime probe is an operational diagnostic, not business functionality: it exercises the
// same mechanisms the fulfillment workflow will use (SQL-guarded execution claim, cross-app
// activities, durable timer, keyed idempotent effects) so a deployment can be checked end to end.

/// <summary>Names shared by the probe workflow and the apps that host its activities.</summary>
public static class RuntimeProbe
{
    public const string WorkflowName = "RuntimeProbeWorkflow";
    public const string ClaimActivity = "ClaimProbeExecution";
    public const string CompleteActivity = "CompleteProbeExecution";
    public const string EffectActivity = "RecordProbeEffect";
    public const string ComputeActivity = "ComputeProbe";

    /// <summary>Failpoint after the effect transaction commits, before the activity result reaches Dapr.</summary>
    public const string AfterEffectCommitFailpoint = "AfterProbeEffectCommitBeforeActivityCompletion";

    public static string InstanceId(string probeId) => $"probe-{probeId}";
}

/// <param name="ProbeId">Caller-chosen id; letters, digits, '-' only.</param>
/// <param name="TimerSeconds">Durable timer between the two effects.</param>
/// <param name="EffectDelayMilliseconds">Work time inside each effect activity (gives drills a window).</param>
public sealed record RuntimeProbeInput(string ProbeId, int TimerSeconds, int EffectDelayMilliseconds);

public sealed record ProbeClaimResult(bool Proceed, int Claims);

public sealed record ProbeEffectInput(string ProbeId, string Step, int DelayMilliseconds);

/// <param name="Applied">True when this attempt created the effect; false when it already existed.</param>
public sealed record ProbeEffectResult(string OperationKey, bool Applied, string Replica);

public sealed record RuntimeProbeResult(
    string Outcome,
    string? Step1Replica,
    string? Step2Replica,
    string? ComputeReplica);
