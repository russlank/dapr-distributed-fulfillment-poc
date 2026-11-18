using Dapr.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fulfillment.UnitTests;

/// <summary>
/// Deterministic in-memory WorkflowContext for unit tests (Dapr.Workflow 1.19 ships no in-memory
/// harness; WorkflowContext is abstract, so a fake can drive a workflow without a runtime).
/// Events are supplied up front; timers either fire immediately or never.
/// </summary>
public sealed class FakeWorkflowContext(string instanceId = "test-1") : WorkflowContext
{
    public Dictionary<string, object?> Events { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TimersFireImmediately { get; init; }

    public List<(string Name, object? Input, string? TargetAppId)> ActivityCalls { get; } = [];

    public Func<string, object?, object?> ActivityResult { get; init; } = (_, _) => "fake";

    public List<DateTime> Timers { get; } = [];

    public object? CustomStatus { get; private set; }

    public override string Name => "fake";

    public override string InstanceId => instanceId;

    public override DateTime CurrentUtcDateTime { get; } = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

    public override bool IsReplaying => false;

    public override bool IsPatched(string patchName) => true;

    public override Task<T> CallActivityAsync<T>(string name, object? input = null, WorkflowTaskOptions? options = null)
    {
        ActivityCalls.Add((name, input, options?.TargetAppId));
        return Task.FromResult((T)ActivityResult(name, input)!);
    }

    public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
    {
        Timers.Add(fireAt);
        if (TimersFireImmediately)
        {
            return Task.CompletedTask;
        }

        var never = new TaskCompletionSource();
        cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
        return never.Task;
    }

    public override Task<T> WaitForExternalEventAsync<T>(string eventName, CancellationToken cancellationToken = default)
    {
        if (Events.TryGetValue(eventName, out var value))
        {
            return Task.FromResult((T)value!);
        }

        var pending = new TaskCompletionSource<T>();
        cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        return pending.Task;
    }

    public override void SendEvent(string instanceId, string eventName, object payload) => throw new NotSupportedException();

    public override void SetCustomStatus(object? customStatus) => CustomStatus = customStatus;

    public override Task<TResult> CallChildWorkflowAsync<TResult>(string workflowName, object? input = null, ChildWorkflowTaskOptions? options = null)
        => throw new NotSupportedException();

    public override ILogger CreateReplaySafeLogger(string categoryName) => NullLogger.Instance;

    public override ILogger CreateReplaySafeLogger(Type type) => NullLogger.Instance;

    public override ILogger CreateReplaySafeLogger<T>() => NullLogger<T>.Instance;

    public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true) => throw new NotSupportedException();

    public override Guid NewGuid() => Guid.Empty;

    public override PropagatedHistory GetPropagatedHistory() => throw new NotSupportedException();
}
