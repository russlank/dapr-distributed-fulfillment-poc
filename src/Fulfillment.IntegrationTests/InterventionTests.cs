using System.Net;
using System.Text;
using System.Text.Json;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.Messaging;
using Fulfillment.OrderApi;
using Fulfillment.OrderApi.Interventions;
using Fulfillment.OrderApi.Orders;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// Resolution of intervention cases against SQL Server: one transaction resolves the case and takes exactly one path
/// (Signal when the original workflow runs, Repair when none waits, RecordOnly on a terminal order); a repeated
/// resolution id is a replay, another one is refused, an action not allowed for the kind is refused, and an
/// unreachable workflow host leaves the case open.
/// </summary>
public sealed class InterventionTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;
    private OrderLifecycleSteps lifecycle = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("cases");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
        lifecycle = new OrderLifecycleSteps(db, StepCheckpoints.None);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await api.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The workflow host's state answer for every instance (null: unreachable).</summary>
    private sealed class WorkflowHostAnswers(string? status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (status is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var body = JsonSerializer.Serialize(new WorkflowStateView("x", status != "Missing", status == "Missing" ? null : status, null, null, null, null), JsonSerializerOptions.Web);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private InterventionResolutions Resolutions(string? workflowStatus) =>
        new(db, new WorkflowHostClient(new SidecarInvoker(new HttpClient(new WorkflowHostAnswers(workflowStatus)) { BaseAddress = new Uri("http://localhost:3500/") },
            new InvocationRetryPolicy(1, TimeSpan.Zero, TimeSpan.Zero))));

    private async Task<(Guid Order, Guid Case)> OpenPaymentCaseAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("cases"), ("SKU-1", 1)), Ct);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;
        await Fulfilling.EnterFulfillmentAsync(Fulfilling.Steps(db), orderId, Ct);
        await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, "PaymentAuthorized"), Ct);
        var opened = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.AmbiguousPayment, "capture-1", "capture undeterminable"), Ct);
        return (orderId, opened.CaseId);
    }

    private static ResolveCaseRequest Resolve(string action = CaseActions.ConfirmExternalSuccess, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "operator-1", action, "provider record checked: Committed");

    [Fact]
    public async Task Resolve_WorkflowRunning_SignalPath_OrderResumes_OneSignal_ReplayAndConflictHandled()
    {
        var (order, caseId) = await OpenPaymentCaseAsync();
        var resolutions = Resolutions("Running");
        var request = Resolve();

        var invalid = await resolutions.ResolveAsync(caseId, Resolve(CaseActions.Acknowledge), Ct);
        var first = await resolutions.ResolveAsync(caseId, request, Ct);
        var replay = await resolutions.ResolveAsync(caseId, request, Ct);
        var other = await resolutions.ResolveAsync(caseId, Resolve(), Ct);

        Assert.Equal(ResolveOutcomeKind.InvalidAction, invalid.Kind);
        Assert.Equal((ResolveOutcomeKind.Resolved, CasePaths.Signal), (first.Kind, first.Resolved!.Path));
        Assert.Equal((ResolveOutcomeKind.Replayed, true), (replay.Kind, replay.Resolved!.Replayed));
        Assert.Equal(ResolveOutcomeKind.AlreadyResolved, other.Kind);
        Assert.Equal("PaymentAuthorized|Resolved|Signal|1|0", await Db.ScalarAsync<string>(db,
            """
            SELECT CONCAT((SELECT Status FROM orders.[Order] WHERE OrderId = @o), '|', c.State, '|', c.ResolutionPath, '|',
              (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o AND EventName = @n), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE CaseId = c.CaseId))
            FROM orders.InterventionCase c WHERE c.CaseId = @c
            """, ("@o", order), ("@n", InterventionSignals.EventName(caseId)), ("@c", caseId)));
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Terminated")]
    [InlineData("Failed")]
    public async Task Resolve_NoWorkflowWaits_RepairPath_StartRequestWritten_OrderStaysInIntervention(string workflowStatus)
    {
        var (order, caseId) = await OpenPaymentCaseAsync();

        var resolved = await Resolutions(workflowStatus).ResolveAsync(caseId, Resolve(), Ct);

        Assert.Equal(CasePaths.Repair, resolved.Resolved!.Path);
        Assert.Equal($"ManualInterventionRequired|Pending|{RepairWorkflow.InstanceId(order, caseId)}|0", await Db.ScalarAsync<string>(db,
            """
            SELECT CONCAT((SELECT Status FROM orders.[Order] WHERE OrderId = @o), '|', r.State, '|', r.InstanceId, '|', (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o))
            FROM orders.RepairStartRequest r WHERE r.CaseId = @c
            """, ("@o", order), ("@c", caseId)));
    }

    [Fact]
    public async Task Resolve_CaseOnACompletedOrder_RecordOnly()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("cases"), ("SKU-1", 1)), Ct);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;
        await Fulfilling.RunAsync(Fulfilling.Steps(db), orderId, Ct);
        var opened = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.CarrierTerminalConflict, "e1", "Exception after Delivered"), Ct);

        var resolved = await Resolutions(null).ResolveAsync(opened.CaseId, Resolve(CaseActions.Acknowledge), Ct);

        Assert.Equal(CasePaths.RecordOnly, resolved.Resolved!.Path);
        Assert.Equal("Completed|0|0", await Db.ScalarAsync<string>(db,
            "SELECT CONCAT((SELECT Status FROM orders.[Order] WHERE OrderId = @o), '|', (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE OrderId = @o))",
            ("@o", orderId)));
    }

    private async Task<(Guid Order, Guid Case)> OpenDeliveryCaseAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("cases"), ("SKU-1", 1)), Ct);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;
        await Fulfilling.EnterFulfillmentAsync(Fulfilling.Steps(db), orderId, Ct);
        foreach (var to in new[] { "PaymentAuthorized", "Packing", "Dispatched", "AwaitingDelivery" })
        {
            await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, to), Ct);
        }

        var opened = await lifecycle.OpenCaseAsync(new OpenCaseRequest(orderId, InterventionKinds.DeliveryOverdue, "delivery-1", "overdue, poll failed"), Ct);
        return (orderId, opened.CaseId);
    }

    [Theory]
    [InlineData("Running", 1)]
    [InlineData("Missing", 0)]
    public async Task DeliveryFailureConfirmed_EscalatesTheOrder_CaseStaysOpenUntilTheBusinessResolutionIsRecorded(string workflowStatus, int signals)
    {
        var (order, caseId) = await OpenDeliveryCaseAsync();
        var resolutions = Resolutions(workflowStatus);
        var confirm = Resolve(CaseActions.ConfirmExternalFailure);

        var acknowledgeWhileOpen = await resolutions.ResolveAsync(caseId, Resolve(CaseActions.Acknowledge), Ct);
        var escalated = await resolutions.ResolveAsync(caseId, confirm, Ct);
        var replay = await resolutions.ResolveAsync(caseId, confirm, Ct);
        var successAfterwards = await resolutions.ResolveAsync(caseId, Resolve(), Ct);
        var seenByTheWorkflow = await lifecycle.ReadCaseAsync(new ReadCaseRequest(caseId), Ct);

        Assert.Equal(ResolveOutcomeKind.InvalidAction, acknowledgeWhileOpen.Kind);
        Assert.Equal((ResolveOutcomeKind.Resolved, CasePaths.Escalate), (escalated.Kind, escalated.Resolved!.Path));
        Assert.Equal((ResolveOutcomeKind.Replayed, CasePaths.Escalate), (replay.Kind, replay.Resolved!.Path));
        Assert.Equal(ResolveOutcomeKind.InvalidAction, successAfterwards.Kind);
        Assert.Equal((CaseStates.Escalated, "Escalated", true), (seenByTheWorkflow.State, seenByTheWorkflow.OrderStatus, seenByTheWorkflow.OrderTerminal));
        Assert.Equal($"Escalated|DeliveryFailed|Closed|Escalated|operator-1||{signals}|0|1", await Db.ScalarAsync<string>(db,
            """
            SELECT CONCAT(o.Status, '|',
              (SELECT TOP 1 ReasonCode FROM orders.OrderStatusHistory WHERE OrderId = @o ORDER BY Sequence DESC), '|',
              (SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = @o), '|', c.State, '|', c.EscalatedBy, '|', c.ResolutionAction, '|',
              (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o AND EventName = @n), '|', (SELECT COUNT(*) FROM orders.RepairStartRequest WHERE CaseId = c.CaseId), '|',
              (SELECT COUNT(*) FROM orders.OrderOutbox x WHERE x.OrderId = @o AND x.Sequence = (SELECT MAX(Sequence) FROM orders.OrderStatusHistory WHERE OrderId = @o)))
            FROM orders.InterventionCase c JOIN orders.[Order] o ON o.OrderId = c.OrderId WHERE c.CaseId = @c
            """, ("@o", order), ("@n", InterventionSignals.EventName(caseId)), ("@c", caseId)));

        var settled = await resolutions.ResolveAsync(caseId, new ResolveCaseRequest(Guid.NewGuid(), "operator-2", CaseActions.Acknowledge, "refunded outside the system, ticket 42"), Ct);
        var lateReplay = await resolutions.ResolveAsync(caseId, confirm, Ct);

        Assert.Equal((ResolveOutcomeKind.Resolved, CasePaths.RecordOnly), (settled.Kind, settled.Resolved!.Path));
        Assert.Equal(ResolveOutcomeKind.Replayed, lateReplay.Kind);
        Assert.Equal("Escalated|Resolved|Acknowledge|RecordOnly|operator-1|operator-2", await Db.ScalarAsync<string>(db,
            """
            SELECT CONCAT(o.Status, '|', c.State, '|', c.ResolutionAction, '|', c.ResolutionPath, '|', c.EscalatedBy, '|', c.ResolvedBy)
            FROM orders.InterventionCase c JOIN orders.[Order] o ON o.OrderId = c.OrderId WHERE c.CaseId = @c
            """, ("@c", caseId)));
    }

    [Fact]
    public async Task EscalatedCase_IsActive_ARepeatedOpeningReturnsIt_AndTheDatabaseRefusesASecondOne()
    {
        var (order, caseId) = await OpenDeliveryCaseAsync();
        await Resolutions("Running").ResolveAsync(caseId, Resolve(CaseActions.ConfirmExternalFailure), Ct);

        var reopened = await lifecycle.OpenCaseAsync(new OpenCaseRequest(order, InterventionKinds.DeliveryOverdue, "delivery-2", "overdue again"), Ct);
        var duplicate = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => Db.ExecuteAsync(db,
            "INSERT orders.InterventionCase (OrderId, Kind, State, OperationKey, Evidence) VALUES (@o, 'DeliveryOverdue', 'Open', 'manual-second', 'x')",
            ("@o", order)));

        Assert.Equal((caseId, CaseStates.Escalated, false), (reopened.CaseId, reopened.State, reopened.Created));
        Assert.Contains("UX_InterventionCase_OpenPerKind", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.InterventionCase WHERE OrderId = @o", ("@o", order)));
    }

    [Fact]
    public async Task Resolve_WorkflowHostUnreachable_LeavesTheCaseOpen()
    {
        var (_, caseId) = await OpenPaymentCaseAsync();

        var outcome = await Resolutions(null).ResolveAsync(caseId, Resolve(), Ct);

        Assert.Equal(ResolveOutcomeKind.WorkflowStateUnknown, outcome.Kind);
        Assert.Equal("Open", await Db.ScalarAsync<string>(db, "SELECT State FROM orders.InterventionCase WHERE CaseId = @c", ("@c", caseId)));
    }
}
