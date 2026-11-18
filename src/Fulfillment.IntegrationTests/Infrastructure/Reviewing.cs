using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Orders;
using Fulfillment.OrderApi.Reviews;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>Drives the review steps and the decision API directly (the workflow's side without Dapr).</summary>
public static class Reviewing
{
    public static ReviewOptions Options(TimeSpan? deadline = null) => new() { Deadline = deadline ?? TimeSpan.FromMinutes(5), TimerGrace = TimeSpan.FromSeconds(2), Rounds = 2 };

    public static ReviewSteps Steps(string connectionString, ReviewOptions? options = null, StepCheckpoint? checkpoint = null) =>
        new(connectionString, options ?? Options(), checkpoint ?? StepCheckpoints.None);

    /// <summary>Validation, checks whose stored risk result requires a review, and round 1: the order is AwaitingApproval.</summary>
    public static async Task<ReviewRound> OpenAsync(string connectionString, Guid orderId, ReviewOptions? options = null, CancellationToken ct = default)
    {
        var steps = Fulfilling.Steps(connectionString);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), ct);
        await steps.RecordChecksAsync(Fulfilling.Checks(orderId, review: true), ct);
        return await Steps(connectionString, options).CreateAsync(new CreateReviewRequest(orderId), ct);
    }

    public static SubmitDecisionRequest Decision(string outcome = DecisionOutcomes.Approve, Guid? id = null, string op = "operator-1", string? comment = null) =>
        new(id ?? Guid.NewGuid(), op, outcome, comment);

    public static async Task<HttpResponseMessage> DecideAsync(HttpClient operatorClient, Guid reviewId, SubmitDecisionRequest decision, CancellationToken ct = default) =>
        await operatorClient.PostAsJsonAsync(ReviewRoutes.Decision(reviewId), decision, ct);

    public static async Task<HttpResponseMessage> DecideRawAsync(HttpClient client, Guid reviewId, string body, CancellationToken ct = default) =>
        await client.PostAsync(ReviewRoutes.Decision(reviewId), new StringContent(body, Encoding.UTF8, "application/json"), ct);

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
        return body is not null && body.TryGetValue("code", out var code) ? code?.ToString() : null;
    }

    public static Task MarkStartedAsync(string connectionString, Guid orderId) =>
        Db.ExecuteAsync(connectionString, $"UPDATE orders.WorkflowStartRequest SET State = 'Started', StartedAt = SYSUTCDATETIME() WHERE OrderId = '{orderId}' AND State = 'Pending'");

    /// <summary>Moves a review's deadline into the past (by SQL time).</summary>
    public static Task ExpireDeadlineAsync(string connectionString, Guid reviewId) =>
        Db.ExecuteAsync(connectionString, $"UPDATE orders.ReviewTask SET DeadlineAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE ReviewId = '{reviewId}'");

    public static async Task<Dictionary<string, object?>> ReviewAsync(string connectionString, Guid reviewId) =>
        (await Db.RowsAsync(connectionString, "SELECT Status, Round, DecisionId, DecidedAt, ExpiredAt FROM orders.ReviewTask WHERE ReviewId = @r", ("@r", reviewId))).Single();

    public static async Task<List<Dictionary<string, object?>>> SignalsAsync(string connectionString, Guid orderId) =>
        await Db.RowsAsync(connectionString,
            "SELECT SignalId, EventName, CausedBy, State, Attempts, LastErrorCode, LeaseToken, ForwardedAt, ClosedAt FROM orders.PendingSignal WHERE OrderId = @o ORDER BY CreatedAt",
            ("@o", orderId));

    public static async Task<int> DecisionsAsync(string connectionString, Guid reviewId) =>
        await Db.ScalarAsync<int>(connectionString, "SELECT COUNT(*) FROM orders.ReviewDecision WHERE ReviewId = @r", ("@r", reviewId));

    public static SignalDispatcher Dispatcher(string connectionString, FakeSignaler signaler, string replica, TimeSpan lease)
    {
        var services = new ServiceCollection().AddSingleton<IWorkflowSignaler>(signaler).BuildServiceProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new SignalDispatcherOptions
        {
            Lease = lease,
            BaseBackoff = TimeSpan.FromSeconds(10),
            MaxBackoff = TimeSpan.FromSeconds(60),
        });
        var identity = new RuntimeIdentity("order-api", replica, "test", null, null, null, null, "release", "absent", "test");
        return new SignalDispatcher(services.GetRequiredService<IServiceScopeFactory>(), new SignalStore(connectionString),
            new Failpoints(null, NullLogger<Failpoints>.Instance), options, identity, NullLogger<SignalDispatcher>.Instance);
    }

    public static Task MakeSignalsDueAsync(string connectionString) =>
        Db.ExecuteAsync(connectionString, "UPDATE orders.PendingSignal SET NextAttemptAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE State = 'Pending'");
}

/// <summary>Stand-in for the workflow host's event route: scripted answers per call, every call recorded.</summary>
public sealed class FakeSignaler : IWorkflowSignaler
{
    private int calls;

    public ConcurrentQueue<(string InstanceId, string EventName, Guid SignalId)> Calls { get; } = new();

    /// <summary>Per call number (1-based): the answer; default Raised.</summary>
    public Func<int, RaiseOutcomeKind>? Answer { get; set; }

    public int CallsFor(Guid signalId) => Calls.Count(c => c.SignalId == signalId);

    public Task<RaiseOutcome> RaiseAsync(string instanceId, string eventName, ReviewSignal payload, Guid signalId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var number = Interlocked.Increment(ref calls);
        Calls.Enqueue((instanceId, eventName, payload.SignalId));
        var kind = Answer?.Invoke(number) ?? RaiseOutcomeKind.Raised;
        var status = kind switch
        {
            RaiseOutcomeKind.Raised => "Running",
            RaiseOutcomeKind.Terminal => "Completed",
            RaiseOutcomeKind.NotFound => "NotFound",
            RaiseOutcomeKind.NotRunning => "Pending",
            _ => null,
        };
        return Task.FromResult(new RaiseOutcome(kind, status, 1, kind is RaiseOutcomeKind.Ambiguous ? 503 : 200, null));
    }
}
