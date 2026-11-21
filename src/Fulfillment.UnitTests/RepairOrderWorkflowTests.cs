using Fulfillment.Contracts;
using Fulfillment.Contracts.Shipping;
using Fulfillment.Contracts.Workflows;
using Fulfillment.WorkflowHost.Orders;
using Xunit;

namespace Fulfillment.UnitTests;

public class RepairOrderWorkflowTests
{
    private static readonly Guid OrderId = Guid.Parse("01a1f000-0000-7000-8000-000000000001");
    private static readonly Guid CaseId = Guid.Parse("01a1f000-0000-7000-8000-0000000000ca");

    private static FakeWorkflowContext Context(string voidOutcome, string? voidCode = null) => new("repair-x")
    {
        ActivityResult = (name, input) => name switch
        {
            InventoryActivities.ReleaseOrderHolds => new ReleaseHoldsResult(1, 0, "inventory-1"),
            PaymentActivities.ExecutePayment when ((PaymentOperationRequest)input!).Step == PaymentSteps.Void =>
                new PaymentOperationResult("k", voidOutcome, voidCode, null, 1, 0, false, "payment-1"),
            PaymentActivities.ExecutePayment => new PaymentOperationResult("k", PaymentOutcomes.Refunded, null, null, 1, 0, false, "payment-1"),
            FulfillmentWorkflow.CloseUnfulfilledOrderActivity => new StepResult(StepOutcomes.Applied, "k", "Compensated"),
            ShippingActivities.GetShipmentStatus => new ShipmentStatusResult(OrderId, "Delivered", "Delivered", null, true),
            OrderLifecycleActivities.FinalizeDelivered => new StepResult(StepOutcomes.Applied, "k", "Completed"),
            RepairWorkflow.RecordRepairOutcomeActivity => new StepResult(StepOutcomes.Applied, null, ""),
            _ => throw new InvalidOperationException(name),
        },
    };

    [Fact]
    public async Task Run_AmbiguousPayment_ReleasesVoidsWithTheRepairKeyCompensatesAndRecords()
    {
        var context = Context(PaymentOutcomes.Voided);

        var result = await new RepairOrderWorkflow().RunAsync(context, new RepairInput(OrderId, CaseId, InterventionKinds.AmbiguousPayment, CaseActions.ConfirmExternalSuccess, 1234, "EUR"));

        Assert.Equal(new PaymentOperationRequest(OrderId, PaymentSteps.Void, PaymentSteps.Repair(CaseId), 1234, "EUR", PaymentSteps.Authorize, "1", true),
            context.ActivityCalls.Select(c => c.Input).OfType<PaymentOperationRequest>().Single());
        Assert.Equal(new RepairOutcomeRequest(CaseId, "Void:Voided"), context.ActivityCalls[^1].Input);
        Assert.Equal(new RepairResult("Void:Voided", "Compensated"), result);
        Assert.Equal(AppIds.InventoryService, context.ActivityCalls[0].TargetAppId);
    }

    [Fact]
    public async Task Run_VoidFindsACapture_RefundsItWithTheRepairKey()
    {
        var context = Context(PaymentOutcomes.Rejected, "AlreadyCaptured");

        var result = await new RepairOrderWorkflow().RunAsync(context, new RepairInput(OrderId, CaseId, InterventionKinds.CompensationExhausted, CaseActions.ConfirmExternalFailure, 1234, "EUR"));

        Assert.Equal([PaymentSteps.Void, PaymentSteps.Refund], context.ActivityCalls.Select(c => c.Input).OfType<PaymentOperationRequest>().Select(r => r.Step).ToArray());
        Assert.Equal("Void:Rejected,Refund:Refunded", result.Outcome);
    }

    [Theory]
    [InlineData(InterventionKinds.WorkflowFaulted, "Packing")]
    [InlineData(InterventionKinds.WorkflowMissing, "AwaitingApproval")]
    public async Task Run_FailedWorkflowBeforeThePointOfNoReturn_CompensatesWithReasonWorkflowFailed(string kind, string status)
    {
        var context = Context(PaymentOutcomes.Voided);
        var result = await new RepairOrderWorkflow().RunAsync(context, new RepairInput(OrderId, CaseId, kind, CaseActions.ConfirmExternalFailure, 1234, "EUR", status));

        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", "WorkflowFailed"),
            context.ActivityCalls.Select(c => c.Input).OfType<CloseUnfulfilledRequest>().Single());
        Assert.Equal(PaymentSteps.Repair(CaseId), context.ActivityCalls.Select(c => c.Input).OfType<PaymentOperationRequest>().Single().Qualifier);
        Assert.Equal(new RepairResult("Void:Voided", "Compensated"), result);
    }

    [Theory]
    [InlineData(InterventionKinds.WorkflowFaulted, CaseActions.ConfirmExternalFailure, "AwaitingDelivery")]
    [InlineData(InterventionKinds.WorkflowMissing, CaseActions.Acknowledge, "Packing")]
    public async Task Run_AfterThePointOfNoReturnOrAcknowledged_RecordsOnly(string kind, string action, string status)
    {
        var context = Context(PaymentOutcomes.Voided);
        var result = await new RepairOrderWorkflow().RunAsync(context, new RepairInput(OrderId, CaseId, kind, action, 1234, "EUR", status));

        Assert.Equal("RecordedOnly", result.Outcome);
        Assert.Equal([RepairWorkflow.RecordRepairOutcomeActivity], context.ActivityCalls.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Run_MismatchConfirmedOnADeliveredShipment_Finalizes()
    {
        var context = Context(PaymentOutcomes.Voided);
        var result = await new RepairOrderWorkflow().RunAsync(context,
            new RepairInput(OrderId, CaseId, InterventionKinds.ExecutionMismatch, CaseActions.ConfirmExternalSuccess, 1234, "EUR", "AwaitingDelivery"));

        Assert.Equal([ShippingActivities.GetShipmentStatus, OrderLifecycleActivities.FinalizeDelivered, RepairWorkflow.RecordRepairOutcomeActivity],
            context.ActivityCalls.Select(c => c.Name).ToArray());
        Assert.Equal(new RepairResult("Finalized:Applied", "Completed"), result);
        Assert.DoesNotContain(context.ActivityCalls, c => c.Input is PaymentOperationRequest);
    }

    [Fact]
    public async Task Run_MismatchWithoutDelivery_DoesNotFinalize()
    {
        var context = new FakeWorkflowContext("repair-x")
        {
            ActivityResult = (name, _) => name switch
            {
                ShippingActivities.GetShipmentStatus => new ShipmentStatusResult(OrderId, "InTransit", null, null, false),
                RepairWorkflow.RecordRepairOutcomeActivity => new StepResult(StepOutcomes.Applied, null, ""),
                _ => throw new InvalidOperationException(name),
            },
        };
        var result = await new RepairOrderWorkflow().RunAsync(context,
            new RepairInput(OrderId, CaseId, InterventionKinds.ExecutionMismatch, CaseActions.ConfirmExternalSuccess, 1234, "EUR", "AwaitingDelivery"));

        Assert.Equal("NotDelivered:InTransit", result.Outcome);
    }
}
