using Fulfillment.Contracts;
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
}
