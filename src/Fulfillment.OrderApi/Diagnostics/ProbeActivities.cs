using Dapr.Workflow;
using Fulfillment.Contracts.Diagnostics;

namespace Fulfillment.OrderApi.Diagnostics;

/// <summary>First probe step: SQL-guarded execution claim (the execution fence).</summary>
public sealed class ClaimProbeExecutionActivity(ProbeStore store) : WorkflowActivity<string, ProbeClaimResult>
{
    public override Task<ProbeClaimResult> RunAsync(WorkflowActivityContext context, string input) => store.ClaimAsync(input);
}

/// <summary>Last probe step: conditional move to Completed.</summary>
public sealed class CompleteProbeExecutionActivity(ProbeStore store) : WorkflowActivity<string, bool>
{
    public override Task<bool> RunAsync(WorkflowActivityContext context, string input) => store.CompleteAsync(input);
}

internal static class ActivityNames
{
    // Registered under the shared contract names, so workflow and activity hosts agree.
    public const string Claim = RuntimeProbe.ClaimActivity;
    public const string Complete = RuntimeProbe.CompleteActivity;
}
