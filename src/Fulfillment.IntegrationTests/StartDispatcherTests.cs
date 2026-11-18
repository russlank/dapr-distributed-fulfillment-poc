using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The start dispatcher against SQL Server: real dispatchers (each with its own identity, like
/// replicas) on one database; only the workflow host is replaced by a fake that behaves like it.
/// Assertions read the start requests and the attempt log.
/// </summary>
public sealed class StartDispatcherTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly TimeSpan LongLease = TimeSpan.FromSeconds(30);
    private static readonly string[] Replicas = ["replica-a", "replica-b", "replica-c"];

    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("dispatch");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await api.DisposeAsync();
    }

    [Fact]
    public async Task RunOnce_DueRequest_StartsThroughTheHostAndMarksStarted()
    {
        var orderId = await AcceptAsync();
        var host = new FakeWorkflowHost();

        var claimed = await Dispatching.Create(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, claimed);
        var call = Assert.Single(host.Calls);
        Assert.Equal((FulfillmentWorkflow.InstanceId(orderId), orderId), call);
        var request = await RequestAsync(orderId);
        Assert.Equal("Started", request["State"]);
        Assert.NotNull(request["StartedAt"]);
        Assert.Equal(1, request["Attempts"]);
        Assert.Null(request["LeaseOwner"]);
        Assert.Null(request["LeaseToken"]);
        Assert.Null(request["LastErrorCode"]);
        Assert.Equal(["Started"], (await AttemptsAsync(orderId)).Select(a => a["Result"]));
    }

    [Fact]
    public async Task RunOnce_InstanceAlreadyExists_MarksStartedWithoutASecondInstance()
    {
        var orderId = await AcceptAsync();
        var host = new FakeWorkflowHost();
        host.Exists(FulfillmentWorkflow.InstanceId(orderId));

        await Dispatching.Create(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Started", (await RequestAsync(orderId))["State"]);
        Assert.Equal(["AlreadyExists"], (await AttemptsAsync(orderId)).Select(a => a["Result"]));
        Assert.Equal(1, host.Instances);
    }

    [Fact]
    public async Task RunOnce_AnswerLostAfterTheInstanceWasCreated_RetriesLaterAndFindsTheInstance()
    {
        var orderId = await AcceptAsync();
        var host = new FakeWorkflowHost { Behavior = (n, _) => n == 1 ? new FakeWorkflowHost.Script(LoseAnswer: true) : FakeWorkflowHost.Script.Normal };
        var dispatcher = Dispatching.Create(db, host, "replica-a", LongLease);

        await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken);

        // Ambiguous: still Pending, lease released, a later attempt is due after the backoff.
        var pending = await RequestAsync(orderId);
        Assert.Equal("Pending", pending["State"]);
        Assert.Equal(StartErrorCodes.StartAmbiguous, pending["LastErrorCode"]);
        Assert.Null(pending["LeaseOwner"]);
        Assert.Equal(1, await Db.ScalarAsync<int>(db,
            "SELECT CASE WHEN NextAttemptAt > SYSUTCDATETIME() THEN 1 ELSE 0 END FROM orders.WorkflowStartRequest WHERE OrderId = @o", ("@o", orderId)));
        Assert.Equal(0, await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken)); // not due yet

        await Dispatching.MakeDueAsync(db);
        await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken);

        var started = await RequestAsync(orderId);
        Assert.Equal("Started", started["State"]);
        Assert.Equal(2, started["Attempts"]);
        Assert.Equal(["Ambiguous", "AlreadyExists"], (await AttemptsAsync(orderId)).Select(a => a["Result"]));
        Assert.Equal(1, host.SchedulesOf(FulfillmentWorkflow.InstanceId(orderId)));
    }

    [Fact]
    public async Task RunOnce_HostRefuses_ReleasesWithLongBackoff()
    {
        var orderId = await AcceptAsync();
        var host = new FakeWorkflowHost { Behavior = (_, _) => new FakeWorkflowHost.Script(Refuse: true) };
        var log = new CapturingLogger();

        await Dispatching.Create(db, host, "replica-a", LongLease, log: log).RunOnceAsync(TestContext.Current.CancellationToken);

        var request = await RequestAsync(orderId);
        Assert.True(Equals(request["LastErrorCode"], StartErrorCodes.StartRejected),
            $"request: {string.Join(", ", request.Select(kv => $"{kv.Key}={kv.Value}"))}; log: {log}");
        Assert.Equal("Pending", request["State"]);
        Assert.Equal(1, await Db.ScalarAsync<int>(db,
            "SELECT CASE WHEN NextAttemptAt > DATEADD(minute, 4, SYSUTCDATETIME()) THEN 1 ELSE 0 END FROM orders.WorkflowStartRequest WHERE OrderId = @o", ("@o", orderId)));
        Assert.Equal(["Rejected"], (await AttemptsAsync(orderId)).Select(a => a["Result"]));
        Assert.Equal(0, host.Instances);
    }

    [Fact]
    public async Task RunOnce_PendingRequestOfTerminalOrder_ClosesItWithoutCallingTheHost()
    {
        var orderId = await AcceptAsync();
        await Db.ExecuteAsync(db, $"UPDATE orders.[Order] SET Status = 'Completed' WHERE OrderId = '{orderId}'");
        var host = new FakeWorkflowHost();

        var claimed = await Dispatching.Create(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, claimed);
        Assert.Empty(host.Calls);
        var request = await RequestAsync(orderId);
        Assert.Equal("Closed", request["State"]);
        Assert.Equal(StartErrorCodes.OrderTerminal, request["LastErrorCode"]);
        Assert.NotNull(request["ClosedAt"]);
        Assert.Null(request["StartedAt"]);
        Assert.Empty(await AttemptsAsync(orderId));
    }

    [Fact]
    public async Task RunOnce_LeaseHeldByAnotherDispatcher_DoesNotClaim()
    {
        var orderId = await AcceptAsync();
        var holder = new WorkflowStartStore(db);
        var held = await holder.ClaimDueAsync("replica-a/holder", 50, LongLease, TestContext.Current.CancellationToken);
        var host = new FakeWorkflowHost();

        var claimed = await Dispatching.Create(db, host, "replica-b", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Contains(held.Claimed, c => c.OrderId == orderId);
        Assert.Equal(0, claimed);
        Assert.Empty(host.Calls);
        Assert.Equal("replica-a/holder", (await RequestAsync(orderId))["LeaseOwner"]);
    }

    [Fact]
    public async Task Dispatcher_CrashedAfterStartingBeforeMarking_IsRecoveredAndCannotOverwriteLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = await AcceptAsync();
        var host = new FakeWorkflowHost();

        // Dispatcher A claims, starts the instance, and dies before recording it (short lease).
        var storeA = new WorkflowStartStore(db);
        var claimA = Assert.Single((await storeA.ClaimDueAsync("replica-a/crashed", 50, TimeSpan.FromMilliseconds(800), ct)).Claimed);
        await host.StartAsync(claimA.InstanceId, new FulfillmentInput(orderId), ct);
        await Dispatching.WaitForLeaseExpiryAsync(db, orderId, ct);

        // Dispatcher B claims the expired lease, finds the instance, and records the start.
        await Dispatching.Create(db, host, "replica-b", LongLease).RunOnceAsync(ct);
        var afterB = await RequestAsync(orderId);
        Assert.Equal("Started", afterB["State"]);
        Assert.Equal(2, afterB["Attempts"]);

        // A comes back with its old token: none of its writes change the row.
        Assert.Equal(FencedUpdateOutcome.LeaseLost, await storeA.MarkStartedAsync(claimA, ct));
        Assert.Equal(FencedUpdateOutcome.LeaseLost, await storeA.ReleaseForRetryAsync(claimA, StartErrorCodes.StartAmbiguous, TimeSpan.FromSeconds(1), ct));
        var final = await RequestAsync(orderId);
        Assert.Equal(afterB["StartedAt"], final["StartedAt"]);
        Assert.Equal("Started", final["State"]);
        Assert.Null(final["LastErrorCode"]);
        Assert.Equal(1, host.SchedulesOf(claimA.InstanceId));
    }

    [Fact]
    public async Task Dispatchers_SlowStartOutlivesItsLease_SecondDispatcherStartsAndTheStaleOneLosesItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = await AcceptAsync();
        var release = new TaskCompletionSource();
        var host = new FakeWorkflowHost { Behavior = (n, _) => n == 1 ? new FakeWorkflowHost.Script(Gate: release.Task) : FakeWorkflowHost.Script.Normal };
        var lease = TimeSpan.FromMilliseconds(800);
        var a = Dispatching.Create(db, host, "replica-a", lease);
        var b = Dispatching.Create(db, host, "replica-b", lease);

        // A's call hangs until released; B claims once the database says A's lease expired.
        var slow = a.RunOnceAsync(ct);
        while (host.Calls.IsEmpty)
        {
            await Task.Delay(50, ct);
        }

        await Dispatching.WaitForLeaseExpiryAsync(db, orderId, ct);
        await b.RunOnceAsync(ct);
        release.SetResult();
        await slow;

        // Both attempts were in flight at the same time; one instance; the stale one changed nothing.
        var attempts = await AttemptsAsync(orderId);
        Assert.Equal(2, attempts.Count);
        var (first, second) = (attempts[0], attempts[1]);
        Assert.StartsWith("replica-a/", (string)first["DispatcherId"]!, StringComparison.Ordinal);
        Assert.StartsWith("replica-b/", (string)second["DispatcherId"]!, StringComparison.Ordinal);
        Assert.True((DateTime)second["CalledAt"]! < (DateTime)first["CompletedAt"]!, "the attempts did not overlap");
        Assert.Equal("LeaseLost", first["Result"]);
        Assert.Equal("Started", second["Result"]);
        Assert.Equal(1, host.Instances);
        var request = await RequestAsync(orderId);
        Assert.Equal("Started", request["State"]);
        Assert.Null(request["LeaseToken"]);
    }

    [Fact]
    public async Task Dispatchers_ThreeConcurrent_StartEveryOrderExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var orders = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            orders.Add(await AcceptAsync());
        }

        var host = new FakeWorkflowHost { Behavior = (_, _) => new FakeWorkflowHost.Script(DelayBefore: TimeSpan.FromMilliseconds(100)) };
        // Small batches, so the dispatchers interleave instead of one claiming everything.
        var dispatchers = Replicas.Select(r => Dispatching.Create(db, host, r, LongLease, batchSize: 5)).ToList();

        // Every dispatcher polls until nothing is left, all at the same time.
        await Task.WhenAll(dispatchers.Select(d => Task.Run(async () =>
        {
            while (await d.RunOnceAsync(ct) > 0)
            {
            }
        }, ct)));

        Assert.Equal(30, host.Calls.Count);
        Assert.All(orders, o => Assert.Equal(1, host.SchedulesOf(FulfillmentWorkflow.InstanceId(o))));
        Assert.Equal(30, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE State = 'Started' AND Attempts = 1"));
        Assert.Equal(30, await Db.ScalarAsync<int>(db, "SELECT COUNT(DISTINCT OrderId) FROM orders.WorkflowStartAttempt WHERE Result = 'Started'"));
        Assert.Equal(30, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.WorkflowStartAttempt"));
        Assert.True(await Db.ScalarAsync<int>(db, "SELECT COUNT(DISTINCT LEFT(DispatcherId, CHARINDEX('/', DispatcherId))) FROM orders.WorkflowStartAttempt") >= 2,
            "only one dispatcher did any work; the test did not exercise concurrency");
    }

    [Fact]
    public async Task HostedDispatcher_InTheApi_StartsAcceptedOrders()
    {
        var host = new FakeWorkflowHost();
        await using var withDispatcher = new OrderApiHost(db,
            s => s.Configure<StartDispatcherOptions>(o => o.PollInterval = TimeSpan.FromMilliseconds(200))
                  .AddSingleton<IWorkflowStarter>(host), dispatcherEnabled: true);
        using var customer = withDispatcher.CreateCustomerClient();
        using var response = await Orders.SubmitAsync(customer, Orders.NewKey(), Orders.Body(Orders.NewCustomer("hosted"), ("SKU-1", 1)), TestContext.Current.CancellationToken);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((string)(await RequestAsync(orderId))["State"]! != "Started" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Equal("Started", (await RequestAsync(orderId))["State"]);
        Assert.Equal(1, host.SchedulesOf(FulfillmentWorkflow.InstanceId(orderId)));
    }

    [Fact]
    public async Task HostedDispatcher_SlowStart_DoesNotBlockOtherStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeWorkflowHost { Behavior = (n, _) => n == 1 ? new FakeWorkflowHost.Script(DelayBefore: TimeSpan.FromSeconds(10)) : FakeWorkflowHost.Script.Normal };
        await using var withDispatcher = new OrderApiHost(db,
            s => s.Configure<StartDispatcherOptions>(o => o.PollInterval = TimeSpan.FromMilliseconds(200))
                  .AddSingleton<IWorkflowStarter>(host), dispatcherEnabled: true);
        using var customer = withDispatcher.CreateCustomerClient();

        using var slowResponse = await Orders.SubmitAsync(customer, Orders.NewKey(), Orders.Body(Orders.NewCustomer("slow"), ("SKU-1", 1)), ct);
        var slow = (await Orders.AcceptedAsync(slowResponse)).OrderId;
        await WaitUntilAsync(() => !host.Calls.IsEmpty, ct);
        using var fastResponse = await Orders.SubmitAsync(customer, Orders.NewKey(), Orders.Body(Orders.NewCustomer("fast"), ("SKU-1", 1)), ct);
        var fast = (await Orders.AcceptedAsync(fastResponse)).OrderId;

        await WaitUntilAsync(async () => (string)(await RequestAsync(fast))["State"]! == "Started", ct);

        // The second order was started while the first start call was still in flight.
        Assert.Equal("Pending", (await RequestAsync(slow))["State"]);
        Assert.Equal(1, host.SchedulesOf(FulfillmentWorkflow.InstanceId(fast)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct) => await WaitUntilAsync(() => Task.FromResult(condition()), ct);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(100, ct);
        }
    }

    [Fact]
    public async Task Dispatcher_WorkflowCompletesBeforeTheStartIsAcknowledged_RecordsAlreadyClosedAndTheTerminalStateStands()
    {
        // Forced order of events, no timing: the fake host creates the instance, the "workflow" runs
        // the real order-api steps to completion (terminal transaction closes the request), and only
        // then does the start answer reach the dispatcher, which still holds its lease token.
        var ct = TestContext.Current.CancellationToken;
        var orderId = await AcceptAsync();
        var steps = Fulfilling.Steps(db);
        var host = new FakeWorkflowHost { Behavior = (_, _) => new FakeWorkflowHost.Script(OnCreated: () => Fulfilling.RunAsync(steps, orderId, ct)) };
        var log = new CapturingLogger();

        await Dispatching.Create(db, host, "replica-a", LongLease, log: log).RunOnceAsync(ct);

        var attempt = Assert.Single(await AttemptsAsync(orderId));
        Assert.Equal("AlreadyClosed", attempt["Result"]);
        var request = await RequestAsync(orderId);
        Assert.Equal("Closed", request["State"]);
        Assert.Null(request["StartedAt"]);
        Assert.Null(request["LeaseToken"]);
        Assert.Null(request["LastErrorCode"]);
        Assert.NotNull(request["ClosedAt"]);
        Assert.Equal(1, request["Attempts"]);
        Assert.Equal("Completed", (await Fulfilling.SnapshotAsync(db, orderId)).Status);
        Assert.DoesNotContain(log.Entries, e => e.Contains("lease lost", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Contains("became terminal meanwhile", StringComparison.Ordinal));

        // Nothing is due any more: no second start.
        Assert.Equal(0, await Dispatching.Create(db, host, "replica-b", LongLease).RunOnceAsync(ct));
        Assert.Equal(1, host.SchedulesOf(FulfillmentWorkflow.InstanceId(orderId)));
    }

    [Fact]
    public async Task Dispatcher_AnswerLostWhileTheOrderCompletes_DoesNotReopenTheClosedRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = await AcceptAsync();
        var steps = Fulfilling.Steps(db);
        var host = new FakeWorkflowHost { Behavior = (_, _) => new FakeWorkflowHost.Script(LoseAnswer: true, OnCreated: () => Fulfilling.RunAsync(steps, orderId, ct)) };

        await Dispatching.Create(db, host, "replica-a", LongLease).RunOnceAsync(ct);

        Assert.Equal("AlreadyClosed", Assert.Single(await AttemptsAsync(orderId))["Result"]);
        var request = await RequestAsync(orderId);
        Assert.Equal("Closed", request["State"]);
        Assert.Null(request["LastErrorCode"]);
    }

    [Fact]
    public async Task Dispatcher_RequestChangedByAnUnknownWriter_IsReportedUnexpectedAndLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = await AcceptAsync();
        // Something other than a dispatcher or a terminal transaction takes the request (not a known transition).
        var host = new FakeWorkflowHost
        {
            Behavior = (_, _) => new FakeWorkflowHost.Script(OnCreated: () => Db.ExecuteAsync(db,
                $"UPDATE orders.WorkflowStartRequest SET State = 'Started', StartedAt = '2026-01-01', LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL WHERE OrderId = '{orderId}'")),
        };
        var log = new CapturingLogger();

        await Dispatching.Create(db, host, "replica-a", LongLease, log: log).RunOnceAsync(ct);

        Assert.Equal("Unexpected", Assert.Single(await AttemptsAsync(orderId))["Result"]);
        var request = await RequestAsync(orderId);
        Assert.Equal(new DateTime(2026, 1, 1), request["StartedAt"]);
        Assert.Contains(log.Entries, e => e.StartsWith("Error:", StringComparison.Ordinal) && e.Contains("changed unexpectedly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatcher_StaleDispatcherAfterCompletion_IsStillALostLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = await AcceptAsync();
        var host = new FakeWorkflowHost();
        var storeA = new WorkflowStartStore(db);
        var claimA = Assert.Single((await storeA.ClaimDueAsync("replica-a/stale", 50, TimeSpan.FromMilliseconds(800), ct)).Claimed);
        await Dispatching.WaitForLeaseExpiryAsync(db, orderId, ct);
        await Dispatching.Create(db, host, "replica-b", LongLease).RunOnceAsync(ct);
        await Fulfilling.RunAsync(Fulfilling.Steps(db), orderId, ct);

        // A was overtaken by B's claim: that it is closed now does not make A's loss anything else.
        Assert.Equal(FencedUpdateOutcome.LeaseLost, await storeA.MarkStartedAsync(claimA, ct));
        Assert.Equal("Closed", (await RequestAsync(orderId))["State"]);
    }

    private async Task<Guid> AcceptAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("dispatch"), ("SKU-1", 1)));
        return (await Orders.AcceptedAsync(response)).OrderId;
    }

    private async Task<Dictionary<string, object?>> RequestAsync(Guid orderId) =>
        (await Db.RowsAsync(db,
            "SELECT State, Attempts, LeaseOwner, LeaseToken, LeaseExpiresAt, LastErrorCode, StartedAt, ClosedAt FROM orders.WorkflowStartRequest WHERE OrderId = @o",
            ("@o", orderId))).Single();

    private async Task<List<Dictionary<string, object?>>> AttemptsAsync(Guid orderId) =>
        await Db.RowsAsync(db, "SELECT DispatcherId, Attempt, CalledAt, CompletedAt, Result FROM orders.WorkflowStartAttempt WHERE OrderId = @o ORDER BY Id", ("@o", orderId));
}
