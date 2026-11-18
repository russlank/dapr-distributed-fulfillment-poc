using System.Collections.Concurrent;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
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

        if (script.Refuse)
        {
            return new StartOutcome(StartOutcomeKind.Rejected, "403");
        }

        // State first, like the management API: an existing instance is reported, never replaced.
        var created = instances.TryAdd(instanceId, 1);
        if (script.LoseAnswer)
        {
            return new StartOutcome(StartOutcomeKind.Ambiguous, null);
        }

        return created ? new StartOutcome(StartOutcomeKind.Started, "Scheduled") : new StartOutcome(StartOutcomeKind.AlreadyExists, "Completed");
    }

    public sealed record Script(TimeSpan DelayBefore = default, bool LoseAnswer = false, bool Refuse = false)
    {
        public static Script Normal { get; } = new();
    }
}

public static class Dispatching
{
    public static WorkflowStartDispatcher Create(string connectionString, FakeWorkflowHost host, string replica, TimeSpan lease, int batchSize = 50)
    {
        var services = new ServiceCollection().AddSingleton<IWorkflowStarter>(host).BuildServiceProvider();
        var options = Options.Create(new StartDispatcherOptions
        {
            Lease = lease,
            BatchSize = batchSize,
            BaseBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(60),
            RejectedBackoff = TimeSpan.FromMinutes(5),
        });
        var identity = new RuntimeIdentity("order-api", replica, "test", null, null, null, null, "release", "absent", "test");
        return new WorkflowStartDispatcher(services.GetRequiredService<IServiceScopeFactory>(), new WorkflowStartStore(connectionString),
            new Failpoints(null, NullLogger<Failpoints>.Instance), options, identity, NullLogger<WorkflowStartDispatcher>.Instance);
    }

    /// <summary>Makes every Pending start request of the database due now (skips the backoff wait).</summary>
    public static Task MakeDueAsync(string connectionString) =>
        Db.ExecuteAsync(connectionString, "UPDATE orders.WorkflowStartRequest SET NextAttemptAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE State = 'Pending'");
}
