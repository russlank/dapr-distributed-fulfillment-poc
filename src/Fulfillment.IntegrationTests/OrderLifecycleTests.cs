using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Orders;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The order-api steps after reservation against SQL Server: forward status steps with their history, the
/// cancellation point of no return (the database refuses an order both cancelled and dispatched), intervention
/// cases opened once per key and once per kind, and closing an unfulfilled order from any running status.
/// </summary>
public sealed class OrderLifecycleTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;
    private FulfillmentSteps steps = null!;
    private OrderLifecycleSteps lifecycle = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("life");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
        steps = Fulfilling.Steps(db);
        lifecycle = new OrderLifecycleSteps(db, StepCheckpoints.None);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await api.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> ReservingAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("life"), ("SKU-1", 1)), Ct);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;
        await Fulfilling.EnterFulfillmentAsync(steps, orderId, Ct);
        return orderId;
    }

    private async Task<Guid> PackingAsync()
    {
        var orderId = await ReservingAsync();
        await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);
        await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "Packing"), Ct);
        return orderId;
    }

    private Task<string> HistoryAsync(Guid orderId) => Db.ScalarAsync<string>(db,
        "SELECT STRING_AGG(CONCAT(ToStatus, ':', ReasonCode), ',') WITHIN GROUP (ORDER BY Sequence) FROM orders.OrderStatusHistory WHERE OrderId = @o", ("@o", orderId));

    [Fact]
    public async Task Advance_ForwardSteps_TransitionOnceEach_RepeatsAndEarlierStepsAreAlreadyApplied()
    {
        var orderId = await ReservingAsync();

        var paid = await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);
        var again = await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);
        var packing = await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "Packing"), Ct);
        var late = await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);

        Assert.Equal([StepOutcomes.Applied, StepOutcomes.AlreadyApplied, StepOutcomes.Applied, StepOutcomes.AlreadyApplied],
            new[] { paid.Outcome, again.Outcome, packing.Outcome, late.Outcome });
        Assert.EndsWith("Reserving:NoReviewRequired,PaymentAuthorized:PaymentAuthorized,Packing:PackingStarted", await HistoryAsync(orderId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeginDispatch_RecordsTheStartOnce_ARepeatIsAlreadyStarted()
    {
        var orderId = await PackingAsync();

        var first = await lifecycle.BeginDispatchAsync(new BeginDispatchRequest(orderId), Ct);
        var second = await lifecycle.BeginDispatchAsync(new BeginDispatchRequest(orderId), Ct);

        Assert.Equal((DispatchOutcomes.Started, DispatchOutcomes.AlreadyStarted), (first.Outcome, second.Outcome));
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId = @o AND DispatchStartedAt IS NOT NULL", ("@o", orderId)));
    }

    [Fact]
    public async Task BeginDispatch_AfterACancellationWasRecorded_RefusesToStart()
    {
        var orderId = await PackingAsync();
        await Db.ExecuteAsync(db, "UPDATE orders.[Order] SET CancelRequestedAt = SYSUTCDATETIME() WHERE OrderId = @o", ("@o", orderId));

        var result = await lifecycle.BeginDispatchAsync(new BeginDispatchRequest(orderId), Ct);

        Assert.Equal(DispatchOutcomes.CancelRequested, result.Outcome);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.[Order] WHERE OrderId = @o AND DispatchStartedAt IS NOT NULL", ("@o", orderId)));
    }

    [Fact]
    public async Task Schema_RefusesAnOrderBothCancelledAndDispatched()
    {
        var orderId = await PackingAsync();
        await lifecycle.BeginDispatchAsync(new BeginDispatchRequest(orderId), Ct);

        var error = await Assert.ThrowsAsync<SqlException>(() =>
            Db.ExecuteAsync(db, "UPDATE orders.[Order] SET CancelRequestedAt = SYSUTCDATETIME() WHERE OrderId = @o", ("@o", orderId)));

        Assert.Contains("CK_Order_CancelOrDispatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenCase_OncePerKeyAndKind_MovesTheOrderToManualIntervention_KeepingItsStatus()
    {
        var orderId = await ReservingAsync();
        await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);

        var opened = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.AmbiguousPayment, "capture-1", "evidence"), Ct);
        var repeated = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.AmbiguousPayment, "capture-1", "evidence"), Ct);
        var otherKey = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.AmbiguousPayment, "capture-2", "evidence"), Ct);

        Assert.True(opened.Created);
        Assert.False(repeated.Created);
        Assert.Equal(opened.CaseId, repeated.CaseId);
        Assert.Equal(opened.CaseId, otherKey.CaseId);
        Assert.Equal(("ManualInterventionRequired", false), (opened.OrderStatus, opened.OrderTerminal));
        Assert.Equal("PaymentAuthorized", await Db.ScalarAsync<string>(db, "SELECT ResumeStatus FROM orders.InterventionCase WHERE CaseId = @c", ("@c", opened.CaseId)));
        Assert.EndsWith("ManualInterventionRequired:InterventionRequired", await HistoryAsync(orderId), StringComparison.Ordinal);
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.InterventionCase WHERE OrderId = @o", ("@o", orderId)));
    }

    [Fact]
    public async Task OpenCase_OnATerminalOrder_AddsHistoryOnly_TheStatusStays()
    {
        var orderId = await ReservingAsync();
        await Fulfilling.CompleteAsync(steps, orderId, Ct);

        var opened = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.CarrierTerminalConflict, "1", "late Exception after Delivered"), Ct);

        Assert.Equal(("Completed", true), (opened.OrderStatus, opened.OrderTerminal));
        Assert.EndsWith("Completed:PostTerminalCase", await HistoryAsync(orderId), StringComparison.Ordinal);
        Assert.Equal(DBNull.Value, (await Db.RowsAsync(db, "SELECT ResumeStatus FROM orders.InterventionCase WHERE CaseId = @c", ("@c", opened.CaseId)))[0]["ResumeStatus"] ?? DBNull.Value);
    }

    [Theory]
    [InlineData("Compensated", "PaymentProviderUnavailable")]
    [InlineData("Rejected", "PaymentDeclined")]
    [InlineData("Cancelled", "CancellationRequested")]
    public async Task CloseUnfulfilled_FromPacking_EndsTheOrderAndClosesItsStartRequest(string status, string reason)
    {
        var orderId = await PackingAsync();

        var closed = await steps.CloseUnfulfilledAsync(new CloseUnfulfilledRequest(orderId, status, reason), Ct);

        Assert.Equal((StepOutcomes.Applied, status), (closed.Outcome, closed.OrderStatus));
        Assert.EndsWith($"{status}:{reason}", await HistoryAsync(orderId), StringComparison.Ordinal);
        Assert.Equal("Closed", await Db.ScalarAsync<string>(db, "SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = @o", ("@o", orderId)));
    }
}
