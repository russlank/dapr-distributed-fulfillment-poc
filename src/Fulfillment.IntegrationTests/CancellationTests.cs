using System.Net;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Orders;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// Customer cancellation against SQL Server (I15): recorded only before the dispatch starts; observed when the
/// fulfillment begins and when a pending review wakes up; refused after the point of no return; and racing the
/// dispatch start, exactly one of the two wins every time, never both.
/// </summary>
public sealed class CancellationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient customer = null!;
    private HttpClient operatorClient = null!;
    private OrderLifecycleSteps lifecycle = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("cancel");
        api = new OrderApiHost(db);
        customer = api.CreateCustomerClient();
        operatorClient = api.CreateOperatorClient();
        lifecycle = new OrderLifecycleSteps(db, StepCheckpoints.None);
    }

    public async ValueTask DisposeAsync()
    {
        customer.Dispose();
        operatorClient.Dispose();
        await api.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> AcceptAsync()
    {
        using var response = await Orders.SubmitAsync(customer, Orders.NewKey(), Orders.Body(Orders.NewCustomer("cancel"), ("SKU-1", 1)), Ct);
        return (await Orders.AcceptedAsync(response)).OrderId;
    }

    private Task<HttpResponseMessage> CancelAsync(Guid orderId) => customer.PostAsync($"/orders/{orderId}/cancel", null, Ct);

    private Task<string> StateAsync(Guid orderId) => Db.ScalarAsync<string>(db,
        """
        SELECT CONCAT(o.Status, '|', CASE WHEN o.CancelRequestedAt IS NULL THEN 0 ELSE 1 END, '|', CASE WHEN o.DispatchStartedAt IS NULL THEN 0 ELSE 1 END, '|',
          (SELECT State FROM orders.WorkflowStartRequest r WHERE r.OrderId = o.OrderId)) FROM orders.[Order] o WHERE o.OrderId = @o
        """, ("@o", orderId));

    [Fact]
    public async Task Cancel_BeforeFulfillmentBegins_Recorded_ThenTheOrderEndsCancelledWithoutReserving()
    {
        var orderId = await AcceptAsync();
        var steps = Fulfilling.Steps(db);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), Ct);
        await steps.RecordChecksAsync(Fulfilling.Checks(orderId), Ct);

        using var first = await CancelAsync(orderId);
        using var again = await CancelAsync(orderId);
        var begun = await steps.BeginFulfillmentAsync(new BeginFulfillmentRequest(orderId), Ct);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));
        Assert.Equal((StepOutcomes.AlreadyTerminal, "Cancelled"), (begun.Outcome, begun.OrderStatus));
        Assert.Equal("Cancelled|1|0|Closed", await StateAsync(orderId));
    }

    [Fact]
    public async Task Cancel_WhileTheReviewWaits_WakesIt_TheResolutionCancels_ADecisionIsRefused()
    {
        var orderId = await AcceptAsync();
        var round = await Reviewing.OpenAsync(db, orderId, ct: Ct);

        using var cancelled = await CancelAsync(orderId);
        var resolution = await Reviewing.Steps(db).ResolveAsync(new ResolveReviewRequest(orderId, round.ReviewId, ReviewWakes.Signal), Ct);
        using var decision = await Reviewing.DecideAsync(operatorClient, round.ReviewId, Reviewing.Decision(), Ct);

        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o AND EventName = @n",
            ("@o", orderId), ("@n", ReviewSignals.EventName(round.ReviewId))));
        Assert.Equal((ReviewStates.Cancelled, "Cancelled", true), (resolution.State, resolution.OrderStatus, resolution.OrderTerminal));
        Assert.Equal((HttpStatusCode.Conflict, ReviewErrorCodes.ReviewCancelled), (decision.StatusCode, await Reviewing.CodeAsync(decision)));
        Assert.Equal("Cancelled|1|0|Closed", await StateAsync(orderId));
    }

    [Fact]
    public async Task Cancel_AfterTheDispatchStarted_IsRefused_AndATerminalOrderToo()
    {
        var dispatched = await AcceptAsync();
        await Fulfilling.EnterFulfillmentAsync(Fulfilling.Steps(db), dispatched, Ct);
        await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(dispatched, "PaymentAuthorized"), Ct);
        await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(dispatched, "Packing"), Ct);
        await lifecycle.BeginDispatchAsync(new BeginDispatchRequest(dispatched), Ct);
        var completed = await AcceptAsync();
        await Fulfilling.RunAsync(Fulfilling.Steps(db), completed, Ct);

        using var late = await CancelAsync(dispatched);
        using var finished = await CancelAsync(completed);
        using var missing = await CancelAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("dispatch-started", await late.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Contains("order-terminal", await finished.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Packing|0|1|Pending", await StateAsync(dispatched));
    }

    [Fact]
    public async Task Cancel_RacingTheDispatchStart_ExactlyOneWins_TwentyRounds()
    {
        var cancels = 0;
        var dispatches = 0;
        for (var i = 0; i < 20; i++)
        {
            var orderId = await AcceptAsync();
            await Fulfilling.EnterFulfillmentAsync(Fulfilling.Steps(db), orderId, Ct);
            await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);
            await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "Packing"), Ct);

            var cancel = Task.Run(() => CancelAsync(orderId), Ct);
            var dispatch = Task.Run(() => lifecycle.BeginDispatchAsync(new BeginDispatchRequest(orderId), Ct), Ct);
            using var cancelled = await cancel;
            var started = await dispatch;

            var cancelWon = cancelled.StatusCode == HttpStatusCode.Accepted;
            var dispatchWon = started.Outcome == DispatchOutcomes.Started;
            Assert.True(cancelWon ^ dispatchWon, $"round {i}: cancel {cancelled.StatusCode}, dispatch {started.Outcome}");
            Assert.Equal(cancelWon ? "Packing|1|0|Pending" : "Packing|0|1|Pending", await StateAsync(orderId));
            cancels += cancelWon ? 1 : 0;
            dispatches += dispatchWon ? 1 : 0;
        }

        Assert.Equal(20, cancels + dispatches);
    }
}
