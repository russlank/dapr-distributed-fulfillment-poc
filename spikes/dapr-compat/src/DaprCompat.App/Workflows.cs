using Dapr.Workflow;

namespace DaprCompat.App;

public static class Apps
{
    public const string Worker = "spike-worker";

    public static WorkflowTaskOptions OnWorker { get; } = new(null, Worker);
}

/// <summary>A2 cross-app activity; workflow distribution across orchestrator replicas.</summary>
public sealed class CrossAppWorkflow : Workflow<string, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, string input)
    {
        context.CreateReplaySafeLogger<CrossAppWorkflow>()
            .LogInformation("workflow {InstanceId} running on {Replica}", context.InstanceId, Replica.Name);
        var workerReplica = await context.CallActivityAsync<string>(
            nameof(RecordActivity), new RecordInput("cross"), Apps.OnWorker);
        // Replica.Name here is the orchestrator replica that ran the final replay (observation only).
        return $"orchestrator={Replica.Name};worker={workerReplica}";
    }
}

public sealed record RecoveryInput(int TimerSeconds, int SlowMilliseconds);

/// <summary>
/// Recovery: step1 (recorded), durable timer, step2 (slow, recorded start/end).
/// Killing the orchestrator during the timer must not re-run step1; killing the worker during
/// step2 re-runs step2 (at-least-once activities).
/// </summary>
public sealed class RecoveryWorkflow : Workflow<RecoveryInput, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, RecoveryInput input)
    {
        var log = context.CreateReplaySafeLogger<RecoveryWorkflow>();
        log.LogInformation("workflow {InstanceId} step1 on {Replica}", context.InstanceId, Replica.Name);
        await context.CallActivityAsync<string>(nameof(RecordActivity), new RecordInput("step1"), Apps.OnWorker);
        context.SetCustomStatus("timer");
        log.LogInformation("workflow {InstanceId} timer armed on {Replica}", context.InstanceId, Replica.Name);
        await context.CreateTimer(TimeSpan.FromSeconds(input.TimerSeconds));
        log.LogInformation("workflow {InstanceId} timer fired on {Replica}", context.InstanceId, Replica.Name);
        context.SetCustomStatus("step2");
        var step2Replica = await context.CallActivityAsync<string>(
            nameof(SlowActivity), new SlowInput("step2", input.SlowMilliseconds), Apps.OnWorker);
        return $"done;orchestrator={Replica.Name};step2={step2Replica}";
    }
}

public sealed record EventInput(int PreTimerSeconds, int TimeoutSeconds, string Mode);

/// <summary>
/// A4/A6: optional timer before waiting, then wait for "go" with a timeout, either through the
/// timeout overload or through WhenAny(event, timer).
/// </summary>
public sealed class EventWorkflow : Workflow<EventInput, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, EventInput input)
    {
        if (input.PreTimerSeconds > 0)
        {
            context.SetCustomStatus("pre-timer");
            await context.CreateTimer(TimeSpan.FromSeconds(input.PreTimerSeconds));
        }

        context.SetCustomStatus("waiting");
        if (input.Mode == "overload")
        {
            try
            {
                var value = await context.WaitForExternalEventAsync<string>("go", TimeSpan.FromSeconds(input.TimeoutSeconds));
                return $"event:{value}";
            }
            catch (OperationCanceledException exception)
            {
                return $"timeout:{exception.GetType().Name}";
            }
        }

        using var cancel = new CancellationTokenSource();
        var eventTask = context.WaitForExternalEventAsync<string>("go", cancel.Token);
        var timerTask = context.CreateTimer(TimeSpan.FromSeconds(input.TimeoutSeconds), cancel.Token);
        var winner = await Task.WhenAny(eventTask, timerTask);
        cancel.Cancel();
        return winner == eventTask ? $"event:{eventTask.Result}" : "timeout:whenany";
    }
}

public sealed record TimerInput(int Seconds);

/// <summary>A7: durable timer across Scheduler restarts; a "nudge" event is the recovery path.</summary>
public sealed class TimerWorkflow : Workflow<TimerInput, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, TimerInput input)
    {
        context.SetCustomStatus("timer-armed");
        using var cancel = new CancellationTokenSource();
        var nudge = context.WaitForExternalEventAsync<string>("nudge", cancel.Token);
        var timer = context.CreateTimer(TimeSpan.FromSeconds(input.Seconds), cancel.Token);
        var winner = await Task.WhenAny(nudge, timer);
        cancel.Cancel();
        var how = winner == timer ? "timer" : "nudge";
        await context.CallActivityAsync<string>(nameof(RecordActivity), new RecordInput($"woken-by-{how}"));
        return how;
    }
}

public sealed record FanOutInput(int Count, int Milliseconds);

/// <summary>A8: fan-out of slow worker activities to observe MaxConcurrentActivities.</summary>
public sealed class FanOutWorkflow : Workflow<FanOutInput, int>
{
    public override async Task<int> RunAsync(WorkflowContext context, FanOutInput input)
    {
        var tasks = Enumerable.Range(0, input.Count)
            .Select(i => context.CallActivityAsync<string>(
                nameof(SlowActivity), new SlowInput($"fan-{i:D2}", input.Milliseconds), Apps.OnWorker))
            .ToList();
        await Task.WhenAll(tasks);
        return tasks.Count;
    }
}

/// <summary>A3/A9: short workflow recording one local execution per run.</summary>
public sealed class ShortWorkflow : Workflow<string, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, string input)
    {
        await context.CallActivityAsync<string>(nameof(RecordActivity), new RecordInput($"short-{input}"));
        return $"short:{input}";
    }
}
