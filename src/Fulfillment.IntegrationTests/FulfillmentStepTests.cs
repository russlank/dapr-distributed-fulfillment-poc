using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Orders;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The fulfillment workflow's OrderDb steps against SQL Server: conditional status transitions with
/// their history, keyed simulated effects, the terminal transaction that closes the start request,
/// and what repeated, concurrent, interrupted, or late executions do. Assertions read the rows.
/// </summary>
public sealed class FulfillmentStepTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Accepted = "1:>Submitted:Accepted";
    private const string Validating = "2:Submitted>Validating:ValidationStarted";
    private const string Completed = "3:Validating>Completed:StubFulfillmentCompleted";

    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("fulfil");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await api.DisposeAsync();
    }

    [Fact]
    public async Task Run_AllSteps_CompletesWithOneEffectPerKeyAndClosesTheStartRequest()
    {
        var orderId = await AcceptAsync(("SKU-1", 2), ("SKU-2", 1));
        var steps = Fulfilling.Steps(db);

        var result = await Fulfilling.RunAsync(steps, orderId, TestContext.Current.CancellationToken);

        Assert.Equal(StepOutcomes.Applied, result.Outcome);
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal("Completed", s.Status);
        Assert.Equal([Accepted, Validating, Completed], s.History);
        Assert.Equal(
            new Dictionary<string, int> { [Key(orderId, "stub-complete:1")] = 0, [Key(orderId, "stub-fulfil:line-1")] = 0, [Key(orderId, "stub-fulfil:line-2")] = 0 },
            s.Effects);
        Assert.Equal(["price:1000:", "risk::10"], s.Checks);
        // Closed straight from Pending (no dispatcher marked it Started): the terminal state is enough.
        Assert.Equal(("Closed", null, false, false, true), (s.RequestState, s.RequestError, s.Leased, s.StartedAtSet, s.ClosedAtSet));
    }

    [Fact]
    public async Task Validate_RepeatedExecution_TransitionsOnce()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        var steps = Fulfilling.Steps(db);
        var request = new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId));

        var first = await steps.ValidateAsync(request, TestContext.Current.CancellationToken);
        var again = await steps.ValidateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal((StepOutcomes.Valid, "Validating"), (first.Outcome, first.OrderStatus));
        Assert.Equal((StepOutcomes.Valid, "Validating"), (again.Outcome, again.OrderStatus));
        Assert.Equal([new ValidatedLine(1, "SKU-1", 1)], again.Lines);
        Assert.Equal([Accepted, Validating], (await Fulfilling.SnapshotAsync(db, orderId)).History);
    }

    [Fact]
    public async Task Validate_ConcurrentExecutions_TransitionOnceWithContiguousHistory()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        var steps = Fulfilling.Steps(db);
        var request = new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => steps.ValidateAsync(request))));

        Assert.All(results, r => Assert.Equal(StepOutcomes.Valid, r.Outcome));
        Assert.Equal([Accepted, Validating], (await Fulfilling.SnapshotAsync(db, orderId)).History);
    }

    [Fact]
    public async Task Validate_DuplicateSku_RejectsAndClosesTheStartRequestTogether()
    {
        var orderId = await AcceptAsync(("SKU-1", 1), ("SKU-1", 2));
        var steps = Fulfilling.Steps(db);
        var request = new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId));

        var result = await steps.ValidateAsync(request, TestContext.Current.CancellationToken);
        var again = await steps.ValidateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal((StepOutcomes.Rejected, "Rejected", "DuplicateSku"), (result.Outcome, result.OrderStatus, result.RuleCode));
        Assert.Equal((StepOutcomes.AlreadyTerminal, "Rejected"), (again.Outcome, again.OrderStatus));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal([Accepted, Validating, "3:Validating>Rejected:DuplicateSku"], s.History);
        Assert.Equal(("Closed", true), (s.RequestState, s.ClosedAtSet));
        Assert.Empty(s.Effects);
    }

    [Fact]
    public async Task Validate_FailureBeforeCommit_LeavesTheOrderSubmitted()
    {
        var orderId = await AcceptAsync(("SKU-1", 1), ("SKU-1", 2));
        var steps = Fulfilling.Steps(db, FailAt(StepCheckpoints.AfterStartRequestClose));

        await Assert.ThrowsAsync<InjectedFault>(() => steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken));

        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(("Submitted", "Pending"), (s.Status, s.RequestState));
        Assert.Equal([Accepted], s.History);
    }

    [Fact]
    public async Task StubStep_RepeatedExecution_IsAppliedOnceAndCountedAsReplay()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        var steps = Fulfilling.Steps(db);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);
        var step = new StubStepRequest(orderId, StubSteps.Fulfil, "line-1");

        var first = await steps.ApplyStubStepAsync(step, TestContext.Current.CancellationToken);
        var second = await steps.ApplyStubStepAsync(step, TestContext.Current.CancellationToken);

        Assert.Equal((StepOutcomes.Applied, Key(orderId, "stub-fulfil:line-1")), (first.Outcome, first.OperationKey));
        Assert.Equal(StepOutcomes.AlreadyApplied, second.Outcome);
        Assert.Equal(new Dictionary<string, int> { [Key(orderId, "stub-fulfil:line-1")] = 1 }, (await Fulfilling.SnapshotAsync(db, orderId)).Effects);
    }

    [Fact]
    public async Task StubStep_CrashAfterCommitBeforeReturn_RetryFindsTheCommittedEffect()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        await Fulfilling.Steps(db).ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);
        var step = new StubStepRequest(orderId, StubSteps.Fulfil, "line-1");

        // The process "dies" after the commit: the caller never gets the result.
        await Assert.ThrowsAsync<InjectedFault>(() => Fulfilling.Steps(db, FailAt(StepCheckpoints.AfterStubEffectCommit)).ApplyStubStepAsync(step, TestContext.Current.CancellationToken));
        var retried = await Fulfilling.Steps(db, replica: "it-replica-2").ApplyStubStepAsync(step, TestContext.Current.CancellationToken);

        Assert.Equal(StepOutcomes.AlreadyApplied, retried.Outcome);
        Assert.Equal(new Dictionary<string, int> { [Key(orderId, "stub-fulfil:line-1")] = 1 }, (await Fulfilling.SnapshotAsync(db, orderId)).Effects);
        Assert.Equal("it-replica", await Db.ScalarAsync<string>(db, "SELECT AppliedBy FROM orders.StubEffect WHERE OrderId = @o", ("@o", orderId)));
    }

    [Theory]
    [InlineData(StepCheckpoints.AfterStatusUpdate)]
    [InlineData(StepCheckpoints.AfterHistoryInsert)]
    [InlineData(StepCheckpoints.AfterEffectInsert)]
    [InlineData(StepCheckpoints.AfterStartRequestClose)]
    public async Task Complete_FailureInsideTheTerminalTransaction_RollsBackEverythingAndRetryCompletes(string point)
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        await Fulfilling.PrepareAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken);
        await MarkStartedAsync(orderId);
        var before = await Fulfilling.SnapshotAsync(db, orderId);

        await Assert.ThrowsAsync<InjectedFault>(() => Fulfilling.CompleteAsync(Fulfilling.Steps(db, FailAt(point, onlyFor: "stub-complete")), orderId, TestContext.Current.CancellationToken));

        var after = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(("Validating", "Started"), (after.Status, after.RequestState));
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.Effects, after.Effects);
        Assert.False(after.ClosedAtSet);

        var retried = await Fulfilling.CompleteAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken);

        Assert.Equal(StepOutcomes.Applied, retried.Outcome);
        var done = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(("Completed", "Closed", true, true), (done.Status, done.RequestState, done.StartedAtSet, done.ClosedAtSet));
        Assert.Equal([Accepted, Validating, Completed], done.History);
        Assert.Equal(0, done.Effects[Key(orderId, "stub-complete:1")]);
    }

    [Fact]
    public async Task Complete_CrashAfterTheTerminalCommit_RetryChangesNothing()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        await Fulfilling.PrepareAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InjectedFault>(() => Fulfilling.CompleteAsync(Fulfilling.Steps(db, FailAt(StepCheckpoints.AfterTerminalCommit)), orderId, TestContext.Current.CancellationToken));
        var committed = await Fulfilling.SnapshotAsync(db, orderId);
        var retried = await Fulfilling.CompleteAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken);

        Assert.Equal(("Completed", "Closed"), (committed.Status, committed.RequestState));
        Assert.Equal((StepOutcomes.AlreadyApplied, "Completed"), (retried.Outcome, retried.OrderStatus));
        var after = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(committed.History, after.History);
        Assert.Equal(1, after.Effects[Key(orderId, "stub-complete:1")]);
    }

    [Fact]
    public async Task Complete_ConcurrentExecutions_OneTerminalTransition()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        await Fulfilling.PrepareAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => Fulfilling.CompleteAsync(Fulfilling.Steps(db, replica: $"replica-{i % 2}"), orderId))));

        Assert.Single(results, r => r.Outcome == StepOutcomes.Applied);
        Assert.Equal(19, results.Count(r => r.Outcome == StepOutcomes.AlreadyApplied));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal([Accepted, Validating, Completed], s.History);
        Assert.Equal(19, s.Effects[Key(orderId, "stub-complete:1")]);
        Assert.Equal("Closed", s.RequestState);
    }

    [Fact]
    public async Task Steps_RacingTheTerminalTransaction_NeverAddAnEffectAfterIt()
    {
        var orderId = await AcceptAsync(("SKU-1", 1), ("SKU-2", 1), ("SKU-3", 1));
        await Fulfilling.Steps(db).ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);

        var steps = Enumerable.Range(1, 3).SelectMany(line => Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => Fulfilling.Steps(db).ApplyStubStepAsync(new StubStepRequest(orderId, StubSteps.Fulfil, $"line-{line}")))));
        var completions = Enumerable.Range(0, 5).Select(_ => Task.Run(() => Fulfilling.CompleteAsync(Fulfilling.Steps(db), orderId)));
        var results = await Task.WhenAll(steps.Concat(completions));

        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { StepOutcomes.Applied, StepOutcomes.AlreadyApplied, StepOutcomes.AlreadyTerminal }));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal([Accepted, Validating, Completed], s.History);
        // The order row lock serializes the steps: no simulated effect was committed after completion.
        Assert.Equal(0, await Db.ScalarAsync<int>(db,
            """
            SELECT COUNT(*) FROM orders.StubEffect e
            WHERE e.OrderId = @o AND e.Step = 'stub-fulfil'
              AND e.AppliedAt > (SELECT AppliedAt FROM orders.StubEffect WHERE OrderId = @o AND Step = 'stub-complete')
            """, ("@o", orderId)));
    }

    [Fact]
    public async Task Steps_TerminalOrder_ChangeNothing()
    {
        var orderId = await AcceptAsync(("SKU-1", 1), ("SKU-2", 1));
        var steps = Fulfilling.Steps(db);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);
        await steps.ApplyStubStepAsync(new StubStepRequest(orderId, StubSteps.Fulfil, "line-1"), TestContext.Current.CancellationToken);
        await Fulfilling.CompleteAsync(steps, orderId, TestContext.Current.CancellationToken);
        var before = await Fulfilling.SnapshotAsync(db, orderId);

        var validate = await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);
        var checks = await steps.RecordChecksAsync(Fulfilling.Checks(orderId), TestContext.Current.CancellationToken);
        var line2 = await steps.ApplyStubStepAsync(new StubStepRequest(orderId, StubSteps.Fulfil, "line-2"), TestContext.Current.CancellationToken);

        Assert.Equal([StepOutcomes.AlreadyTerminal, StepOutcomes.AlreadyTerminal, StepOutcomes.AlreadyTerminal], new[] { validate.Outcome, checks.Outcome, line2.Outcome });
        var after = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.Effects, after.Effects);
        Assert.Empty(after.Checks);
    }

    [Fact]
    public async Task Complete_OrderNotValidating_FailsWithoutChange()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Fulfilling.CompleteAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken));

        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(("Submitted", "Pending"), (s.Status, s.RequestState));
        Assert.Equal([Accepted], s.History);
        Assert.Empty(s.Effects);
    }

    [Fact]
    public async Task RecordChecks_RepeatedExecution_KeepsTheFirstResult()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));
        var steps = Fulfilling.Steps(db);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);

        var first = await steps.RecordChecksAsync(Fulfilling.Checks(orderId, total: 1000, score: 10), TestContext.Current.CancellationToken);
        var second = await steps.RecordChecksAsync(Fulfilling.Checks(orderId, total: 2000, score: 20), TestContext.Current.CancellationToken);

        Assert.Equal((StepOutcomes.Applied, StepOutcomes.AlreadyApplied), (first.Outcome, second.Outcome));
        Assert.Equal(["price:1000:", "risk::10"], (await Fulfilling.SnapshotAsync(db, orderId)).Checks);
        Assert.Equal(2, await Db.ScalarAsync<int>(db, "SELECT SUM(Replays) FROM orders.OrderCheck WHERE OrderId = @o", ("@o", orderId)));
    }

    [Fact]
    public async Task StubStep_CompletionStepName_IsRefused()
    {
        var orderId = await AcceptAsync(("SKU-1", 1));

        await Assert.ThrowsAsync<ArgumentException>(() => Fulfilling.Steps(db).ApplyStubStepAsync(new StubStepRequest(orderId, StubSteps.Complete, "1"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Fulfilling.Steps(db).ApplyStubStepAsync(new StubStepRequest(orderId, "reserve", "line-1"), TestContext.Current.CancellationToken));
    }

    private static string Key(Guid orderId, string rest) => $"{orderId:N}:{rest}";

    /// <summary>A checkpoint that throws at one named point (optionally only for keys containing a text).</summary>
    private static StepCheckpoint FailAt(string point, string? onlyFor = null) => (name, key, _) =>
        name == point && (onlyFor is null || key.Contains(onlyFor, StringComparison.Ordinal)) ? throw new InjectedFault(point) : Task.CompletedTask;

    private Task MarkStartedAsync(Guid orderId) =>
        Db.ExecuteAsync(db, $"UPDATE orders.WorkflowStartRequest SET State = 'Started', StartedAt = SYSUTCDATETIME() WHERE OrderId = '{orderId}'");

    private async Task<Guid> AcceptAsync(params (string Sku, int Quantity)[] lines)
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("fulfil"), lines));
        return (await Orders.AcceptedAsync(response)).OrderId;
    }

    private sealed class InjectedFault(string point) : Exception($"injected fault at {point}");
}
