using Dapr.Workflow;

namespace DaprCompat.App;

public sealed record RecordInput(string Step);

public sealed record SlowInput(string Step, int Milliseconds);

/// <summary>Records one execution and returns the executing replica.</summary>
public sealed class RecordActivity(Evidence evidence) : WorkflowActivity<RecordInput, string>
{
    public override async Task<string> RunAsync(WorkflowActivityContext context, RecordInput input)
    {
        await evidence.AddAsync("activity", $"{context.InstanceId}:{input.Step}", "exec");
        return Replica.Name;
    }
}

/// <summary>Records start, waits, records end. Two starts and one end means the activity ran again.</summary>
public sealed class SlowActivity(Evidence evidence) : WorkflowActivity<SlowInput, string>
{
    public override async Task<string> RunAsync(WorkflowActivityContext context, SlowInput input)
    {
        var key = $"{context.InstanceId}:{input.Step}";
        await evidence.AddAsync("activity-start", key);
        await Task.Delay(input.Milliseconds);
        await evidence.AddAsync("activity-end", key);
        return Replica.Name;
    }
}
