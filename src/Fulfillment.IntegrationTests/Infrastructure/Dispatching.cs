using System.Collections.Concurrent;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>
/// Stand-in for the workflow host that behaves like it: one instance per id, created on the first
/// start, reported as existing afterwards. Scripts make chosen calls slow, lost (the instance is
/// created but the answer never arrives), or refused.
/// </summary>
public sealed class FakeWorkflowHost : IWorkflowStarter
{
    private readonly ConcurrentDictionary<string, int> instances = new(StringComparer.Ordinal);
    private int calls;

    public ConcurrentQueue<(string InstanceId, Guid OrderId)> Calls { get; } = new();

    /// <summary>Per call number (1-based): what to do instead of answering normally.</summary>
    public Func<int, string, Script>? Behavior { get; set; }

    public int Instances => instances.Count;

    public int SchedulesOf(string instanceId) => instances.TryGetValue(instanceId, out var n) ? n : 0;

    public void Exists(string instanceId) => instances.TryAdd(instanceId, 1);

    public async Task<StartOutcome> StartAsync(string instanceId, FulfillmentInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var number = Interlocked.Increment(ref calls);
        Calls.Enqueue((instanceId, input.OrderId));
        var script = Behavior?.Invoke(number, instanceId) ?? Script.Normal;
        if (script.DelayBefore > TimeSpan.Zero)
        {
            await Task.Delay(script.DelayBefore, ct);
        }

        if (script.Gate is not null)
        {
            await script.Gate.WaitAsync(ct);
        }

        if (script.Refuse)
        {
            return new StartOutcome(StartOutcomeKind.Rejected, "403");
        }

        // State first, like the management API: an existing instance is reported, never replaced.
        var created = instances.TryAdd(instanceId, 1);
        if (created && script.OnCreated is not null)
        {
            // What the new instance does before the answer reaches the dispatcher (e.g. run to completion).
            await script.OnCreated();
        }

        if (script.LoseAnswer)
        {
            return new StartOutcome(StartOutcomeKind.Ambiguous, null);
        }

        return created ? new StartOutcome(StartOutcomeKind.Started, "Scheduled") : new StartOutcome(StartOutcomeKind.AlreadyExists, "Completed");
    }

    /// <summary>Gate: the call waits until the test completes the task (deterministic "slow call").</summary>
    /// <summary>OnCreated: runs after the instance was created and before the answer is returned.</summary>
    public sealed record Script(TimeSpan DelayBefore = default, bool LoseAnswer = false, bool Refuse = false, Task? Gate = null, Func<Task>? OnCreated = null)
    {
        public static Script Normal { get; } = new();
    }
}

public static class Dispatching
{
    public static WorkflowStartDispatcher Create(string connectionString, FakeWorkflowHost host, string replica, TimeSpan lease, int batchSize = 50, CapturingLogger? log = null)
    {
        var services = new ServiceCollection().AddSingleton<IWorkflowStarter>(host).BuildServiceProvider();
        var options = Options.Create(new StartDispatcherOptions
        {
            Lease = lease,
            BatchSize = batchSize,
            BaseBackoff = TimeSpan.FromSeconds(10), // large against server clock steps
            MaxBackoff = TimeSpan.FromSeconds(60),
            RejectedBackoff = TimeSpan.FromMinutes(5),
        });
        var identity = new RuntimeIdentity("order-api", replica, "test", null, null, null, null, "release", "absent", "test");
        return new WorkflowStartDispatcher(services.GetRequiredService<IServiceScopeFactory>(), new WorkflowStartStore(connectionString),
            new Failpoints(null, NullLogger<Failpoints>.Instance), options, identity, (ILogger<WorkflowStartDispatcher>?)log ?? NullLogger<WorkflowStartDispatcher>.Instance);
    }

    /// <summary>
    /// Waits until the database itself considers the order's lease expired. Lease decisions use the
    /// server clock only, which can step (observed: back by about 2 s in the WSL2-hosted container
    /// under load), so tests never assume that a client-side sleep expired a lease.
    /// </summary>
    public static async Task WaitForLeaseExpiryAsync(string connectionString, Guid orderId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await Db.ScalarAsync<int>(connectionString,
                   "SELECT CASE WHEN LeaseExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END FROM orders.WorkflowStartRequest WHERE OrderId = @o", ("@o", orderId)) == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the lease did not expire by server time");
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// Makes every Pending start request of the database due (skips the backoff wait). The margin is
    /// far beyond the server clock's backward steps (<see cref="SqlClock"/>).
    /// </summary>
    public static Task MakeDueAsync(string connectionString) =>
        Db.ExecuteAsync(connectionString, $"UPDATE orders.WorkflowStartRequest SET NextAttemptAt = DATEADD(second, -{SqlClock.MarginSeconds}, SYSUTCDATETIME()) WHERE State = 'Pending'");
}

/// <summary>Keeps the dispatcher's log entries (with exceptions) for failure messages.</summary>
public sealed class CapturingLogger : ILogger<WorkflowStartDispatcher>
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
    }

    public override string ToString() => string.Join(" || ", Entries);
}
