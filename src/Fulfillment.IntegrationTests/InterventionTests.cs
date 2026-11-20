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

    [Fact]
    public async Task Resolve_WorkflowHostUnreachable_LeavesTheCaseOpen()
    {
        var (_, caseId) = await OpenPaymentCaseAsync();

        var outcome = await Resolutions(null).ResolveAsync(caseId, Resolve(), Ct);

        Assert.Equal(ResolveOutcomeKind.WorkflowStateUnknown, outcome.Kind);
        Assert.Equal("Open", await Db.ScalarAsync<string>(db, "SELECT State FROM orders.InterventionCase WHERE CaseId = @c", ("@c", caseId)));
    }
}
