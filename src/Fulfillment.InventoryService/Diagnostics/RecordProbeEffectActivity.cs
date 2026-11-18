using Dapr.Workflow;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Primitives;
using Fulfillment.ServiceDefaults;

namespace Fulfillment.InventoryService.Diagnostics;

/// <summary>
/// Keyed probe effect. The activity may run more than once (Dapr activities are at least once);
/// the operation key makes the effect itself happen once. The failpoint sits after the commit and
/// before the result returns to Dapr, the window in which a crash makes Dapr run the activity again.
/// </summary>
public sealed class RecordProbeEffectActivity(ProbeEffectStore store, Failpoints failpoints, RuntimeIdentity identity)
    : WorkflowActivity<ProbeEffectInput, ProbeEffectResult>
{
    public override async Task<ProbeEffectResult> RunAsync(WorkflowActivityContext context, ProbeEffectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var key = OperationKey.For(input.ProbeId, "probe-effect", input.Step).Value;
        if (input.DelayMilliseconds > 0)
        {
            await Task.Delay(input.DelayMilliseconds);
        }

        var applied = await store.RecordAsync(key, identity.Replica);
        await failpoints.HitAsync(RuntimeProbe.AfterEffectCommitFailpoint, key);
        return new ProbeEffectResult(key, applied, identity.Replica);
    }
}
