using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;

namespace Fulfillment.WorkflowHost.Diagnostics;

/// <summary>
/// Runtime probe: claim (order-api, SQL fence) → effect step1 (inventory-service) → durable timer →
/// effect step2 (inventory-service) → compute (compute-worker) → complete (order-api).
/// Deterministic: no clock, random, or I/O here; every effect carries an operation key derived
/// from the probe id and the step, so retried or replayed steps are recognized by the receiver.
/// </summary>
public sealed partial class RuntimeProbeWorkflow : Workflow<RuntimeProbeInput, RuntimeProbeResult>
{
    private static readonly WorkflowTaskOptions OnOrderApi = new(null, AppIds.OrderApi);
    private static readonly WorkflowTaskOptions OnInventory = new(null, AppIds.InventoryService);
    private static readonly WorkflowTaskOptions OnCompute = new(null, AppIds.ComputeWorker);

    public override async Task<RuntimeProbeResult> RunAsync(WorkflowContext context, RuntimeProbeInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var log = context.CreateReplaySafeLogger<RuntimeProbeWorkflow>();

        var claim = await context.CallActivityAsync<ProbeClaimResult>(RuntimeProbe.ClaimActivity, input.ProbeId, OnOrderApi);
        if (!claim.Proceed)
        {
            LogFenced(log, context.InstanceId);
            return new RuntimeProbeResult("AlreadyTerminal", null, null, null);
        }

        context.SetCustomStatus("step1");
        var step1 = await context.CallActivityAsync<ProbeEffectResult>(
            RuntimeProbe.EffectActivity, new ProbeEffectInput(input.ProbeId, "step1", input.EffectDelayMilliseconds), OnInventory);

        context.SetCustomStatus("timer");
        LogTimer(log, context.InstanceId, input.TimerSeconds);
        await context.CreateTimer(TimeSpan.FromSeconds(input.TimerSeconds));

        context.SetCustomStatus("step2");
        var step2 = await context.CallActivityAsync<ProbeEffectResult>(
            RuntimeProbe.EffectActivity, new ProbeEffectInput(input.ProbeId, "step2", input.EffectDelayMilliseconds), OnInventory);

        var compute = await context.CallActivityAsync<string>(RuntimeProbe.ComputeActivity, input.ProbeId, OnCompute);
        await context.CallActivityAsync<bool>(RuntimeProbe.CompleteActivity, input.ProbeId, OnOrderApi);
        context.SetCustomStatus("completed");
        return new RuntimeProbeResult("Completed", step1.Replica, step2.Replica, compute);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "probe {InstanceId} fenced: already terminal, no effects")]
    private static partial void LogFenced(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "probe {InstanceId} timer armed for {Seconds}s")]
    private static partial void LogTimer(ILogger logger, string instanceId, int seconds);
}
