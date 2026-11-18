using DaprCompat.App;
using Xunit;

namespace DaprCompat.Tests;

public class WorkflowTests
{
    [Theory]
    [InlineData("overload")]
    [InlineData("whenany")]
    public async Task EventWins_WhenEventIsAvailable(string mode)
    {
        var context = new FakeWorkflowContext { Events = { ["go"] = "early" } };

        var result = await new EventWorkflow().RunAsync(context, new EventInput(0, 60, mode));

        Assert.Equal("event:early", result);
        Assert.Equal("waiting", context.CustomStatus);
    }

    [Theory]
    [InlineData("overload", "timeout:")]
    [InlineData("whenany", "timeout:whenany")]
    public async Task TimeoutWins_WhenNoEventArrives(string mode, string expectedPrefix)
    {
        var context = new FakeWorkflowContext { TimersFireImmediately = true };

        var result = await new EventWorkflow().RunAsync(context, new EventInput(0, 5, mode));

        Assert.StartsWith(expectedPrefix, result, StringComparison.Ordinal);
        Assert.Contains(context.CurrentUtcDateTime.AddSeconds(5), context.Timers);
    }

    [Fact]
    public async Task Recovery_CallsWorkerActivitiesInOrder_WithTargetAppId()
    {
        var context = new FakeWorkflowContext { TimersFireImmediately = true, ActivityResult = (_, _) => "worker-x" };

        var result = await new RecoveryWorkflow().RunAsync(context, new RecoveryInput(20, 500));

        Assert.Equal(
            [(nameof(RecordActivity), "spike-worker"), (nameof(SlowActivity), "spike-worker")],
            context.ActivityCalls.Select(c => (c.Name, c.TargetAppId)).ToArray());
        Assert.Equal([context.CurrentUtcDateTime.AddSeconds(20)], context.Timers);
        Assert.StartsWith("done;", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timer_NudgeWins_WhenTimerNeverFires()
    {
        var context = new FakeWorkflowContext { Events = { ["nudge"] = "recover" } };

        var result = await new TimerWorkflow().RunAsync(context, new TimerInput(20));

        Assert.Equal("nudge", result);
        Assert.Equal("woken-by-nudge", ((RecordInput)context.ActivityCalls.Single().Input!).Step);
    }
}
