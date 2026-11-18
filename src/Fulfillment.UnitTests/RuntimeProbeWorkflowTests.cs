using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.WorkflowHost.Diagnostics;
using Xunit;

namespace Fulfillment.UnitTests;

public class RuntimeProbeWorkflowTests
{
    [Fact]
    public async Task Run_ClaimRejected_ExitsWithoutEffects()
    {
        var context = new FakeWorkflowContext("probe-p1")
        {
            TimersFireImmediately = true,
            ActivityResult = (name, _) => name == RuntimeProbe.ClaimActivity ? new ProbeClaimResult(false, 0) : null,
        };

        var result = await new RuntimeProbeWorkflow().RunAsync(context, new RuntimeProbeInput("p1", 5, 0));

        Assert.Equal("AlreadyTerminal", result.Outcome);
        Assert.Equal([RuntimeProbe.ClaimActivity], context.ActivityCalls.Select(c => c.Name).ToArray());
        Assert.Empty(context.Timers);
    }

    [Fact]
    public async Task Run_Claimed_RunsStepsInOrderOnOwningApps()
    {
        var context = new FakeWorkflowContext("probe-p2")
        {
            TimersFireImmediately = true,
            ActivityResult = (name, input) => name switch
            {
                RuntimeProbe.ClaimActivity => new ProbeClaimResult(true, 1),
                RuntimeProbe.EffectActivity => new ProbeEffectResult("k", true, "inventory-1"),
                RuntimeProbe.ComputeActivity => "compute-1",
                _ => true,
            },
        };

        var result = await new RuntimeProbeWorkflow().RunAsync(context, new RuntimeProbeInput("p2", 7, 0));

        Assert.Equal(
            [
                (RuntimeProbe.ClaimActivity, AppIds.OrderApi),
                (RuntimeProbe.EffectActivity, AppIds.InventoryService),
                (RuntimeProbe.EffectActivity, AppIds.InventoryService),
                (RuntimeProbe.ComputeActivity, AppIds.ComputeWorker),
                (RuntimeProbe.CompleteActivity, AppIds.OrderApi),
            ],
            context.ActivityCalls.Select(c => (c.Name, c.TargetAppId!)).ToArray());
        Assert.Equal(["step1", "step2"], context.ActivityCalls.Select(c => c.Input).OfType<ProbeEffectInput>().Select(i => i.Step).ToArray());
        Assert.Equal([context.CurrentUtcDateTime.AddSeconds(7)], context.Timers);
        Assert.Equal(new RuntimeProbeResult("Completed", "inventory-1", "inventory-1", "compute-1"), result);
    }
}
