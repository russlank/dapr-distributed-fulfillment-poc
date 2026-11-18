using System.Net;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.OrderApi.Reviews;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The signal dispatcher against SQL Server, with signals written by real decisions; only the
/// workflow host's event route is replaced by a scripted fake. Assertions read the signal rows and
/// the attempt log: a signal is Forwarded only after a raise to a running instance.
/// </summary>
public sealed class SignalDispatcherTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly TimeSpan LongLease = TimeSpan.FromSeconds(30);
    private static readonly string[] Replicas = ["replica-a", "replica-b", "replica-c"];
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;
    private HttpClient operators = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("signals");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
        operators = api.CreateOperatorClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        operators.Dispose();
        await api.DisposeAsync();
    }

    [Fact]
    public async Task RunningInstance_IsRaisedOnceAndForwarded()
    {
        var (orderId, signalId, eventName) = await DecidedAsync();
        var host = new FakeSignaler();

        await Reviewing.Dispatcher(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        var call = Assert.Single(host.Calls);
        Assert.Equal(($"fulfill-{orderId:N}", eventName, signalId), call);
        var signal = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        Assert.Equal(("Forwarded", null), ((string)signal["State"]!, signal["LeaseToken"]));
        Assert.NotNull(signal["ForwardedAt"]);
        Assert.Equal(["Forwarded"], await ResultsAsync(signalId));
        // Delivered: nothing more to do.
        Assert.Equal(0, await Reviewing.Dispatcher(db, host, "replica-b", LongLease).RunOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkflowNotStartedYet_IsKeptPendingWithoutARaiseAndDeliveredLater()
    {
        var (orderId, signalId, _) = await DecidedAsync(markStarted: false);
        var host = new FakeSignaler();

        await Reviewing.Dispatcher(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);
        var waiting = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        await Reviewing.MarkStartedAsync(db, orderId);
        await Reviewing.MakeSignalsDueAsync(db);
        await Reviewing.Dispatcher(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("Pending", "WorkflowNotStarted"), ((string)waiting["State"]!, (string)waiting["LastErrorCode"]!));
        Assert.Equal(1, host.CallsFor(signalId));
        Assert.Equal(["NotStarted", "Forwarded"], await ResultsAsync(signalId));
        Assert.Equal("Forwarded", Assert.Single(await Reviewing.SignalsAsync(db, orderId))["State"]);
    }

    [Fact]
    public async Task TerminalInstance_IsDiscardedAndTheDecisionStays()
    {
        var (orderId, signalId, _) = await DecidedAsync();
        var host = new FakeSignaler { Answer = _ => RaiseOutcomeKind.Terminal };

        await Reviewing.Dispatcher(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        var signal = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        Assert.Equal(("Discarded", "WorkflowTerminal"), ((string)signal["State"]!, (string)signal["LastErrorCode"]!));
        Assert.Equal(["Discarded"], await ResultsAsync(signalId));
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.ReviewDecision WHERE OrderId = @o", ("@o", orderId)));
    }

    [Fact]
    public async Task MissingInstanceAlthoughStarted_IsStuckForTheReconciler()
    {
        var (orderId, _, _) = await DecidedAsync();
        var host = new FakeSignaler { Answer = _ => RaiseOutcomeKind.NotFound };

        await Reviewing.Dispatcher(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        var signal = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        Assert.Equal(("Stuck", "WorkflowMissing"), ((string)signal["State"]!, (string)signal["LastErrorCode"]!));
    }

    [Theory]
    [InlineData(RaiseOutcomeKind.Ambiguous, "RaiseAmbiguous")]
    [InlineData(RaiseOutcomeKind.NotRunning, "WorkflowNotRunning")]
    public async Task UnusableAnswer_IsRetriedWithBackoffUntilForwarded(RaiseOutcomeKind first, string code)
    {
        var (orderId, signalId, _) = await DecidedAsync();
        var host = new FakeSignaler { Answer = n => n == 1 ? first : RaiseOutcomeKind.Raised };

        await Reviewing.Dispatcher(db, host, "replica-a", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);
        var held = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        // Backoff: not due again at once.
        Assert.Equal(0, await Reviewing.Dispatcher(db, host, "replica-b", LongLease).RunOnceAsync(TestContext.Current.CancellationToken));
        await Reviewing.MakeSignalsDueAsync(db);
        await Reviewing.Dispatcher(db, host, "replica-b", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("Pending", code, null), ((string)held["State"]!, (string)held["LastErrorCode"]!, held["ForwardedAt"]));
        Assert.Equal(2, host.CallsFor(signalId));
        Assert.Equal("Forwarded", Assert.Single(await Reviewing.SignalsAsync(db, orderId))["State"]);
    }

    [Fact]
    public async Task CrashAfterRaiseBeforeMarking_IsRaisedAgainAfterTheLeaseAndTheStaleOwnerChangesNothing()
    {
        var (orderId, signalId, eventName) = await DecidedAsync();
        var host = new FakeSignaler();
        var store = new SignalStore(db);

        // Dispatcher A claims and raises, then "dies" before recording anything (short lease).
        var claimA = Assert.Single(await store.ClaimDueAsync("replica-a/crashed", 10, TimeSpan.FromMilliseconds(800), TestContext.Current.CancellationToken));
        await host.RaiseAsync(claimA.InstanceId, claimA.EventName, new Fulfillment.Contracts.Workflows.ReviewSignal(signalId), signalId, TestContext.Current.CancellationToken);
        await WaitForLeaseExpiryAsync(signalId);
        await Reviewing.Dispatcher(db, host, "replica-b", LongLease).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, host.CallsFor(signalId));
        Assert.All(host.Calls, c => Assert.Equal(eventName, c.EventName));
        Assert.Equal(FencedUpdateOutcome.LeaseLost, await store.MarkForwardedAsync(claimA, TestContext.Current.CancellationToken));
        var signal = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        Assert.Equal(("Forwarded", 2), ((string)signal["State"]!, (int)signal["Attempts"]!));
    }

    [Fact]
    public async Task ConcurrentDispatchers_EachSignalIsClaimedAndRaisedOnce()
    {
        var signals = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            signals.Add((await DecidedAsync()).SignalId);
        }

        var host = new FakeSignaler();
        var dispatchers = Replicas.Select(r => Reviewing.Dispatcher(db, host, r, LongLease)).ToList();

        await Task.WhenAll(dispatchers.Select(d => Task.Run(() => d.RunOnceAsync(TestContext.Current.CancellationToken))));
        await Task.WhenAll(dispatchers.Select(d => Task.Run(() => d.RunOnceAsync(TestContext.Current.CancellationToken))));

        Assert.All(signals, s => Assert.Equal(1, host.CallsFor(s)));
        Assert.Equal(12, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.PendingSignal WHERE State = 'Forwarded'"));
    }

    private async Task<List<string?>> ResultsAsync(Guid signalId) =>
        [.. (await Db.RowsAsync(db, "SELECT Result FROM orders.SignalAttempt WHERE SignalId = @s ORDER BY Id", ("@s", signalId))).Select(r => (string?)r["Result"])];

    /// <summary>An order waiting for review, approved through the endpoint: one pending signal.</summary>
    private async Task<(Guid OrderId, Guid SignalId, string EventName)> DecidedAsync(bool markStarted = true)
    {
        using var accepted = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("signals"), ("SKU-1", 1)));
        var orderId = (await Orders.AcceptedAsync(accepted)).OrderId;
        var review = await Reviewing.OpenAsync(db, orderId);
        if (markStarted)
        {
            await Reviewing.MarkStartedAsync(db, orderId);
        }

        using var decided = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision());
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var signal = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        return (orderId, (Guid)signal["SignalId"]!, (string)signal["EventName"]!);
    }

    private async Task WaitForLeaseExpiryAsync(Guid signalId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await Db.ScalarAsync<int>(db,
                   "SELECT CASE WHEN LeaseExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END FROM orders.PendingSignal WHERE SignalId = @s", ("@s", signalId)) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the signal lease did not expire by server time");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
