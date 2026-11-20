using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>
/// The repair of a resolved intervention case whose original workflow no longer waits (terminal, failed, or missing).
/// Payment cases (AmbiguousPayment, CompensationExhausted): release the holds, void the authorization's key with the
/// repair key (a void that finds a capture is followed by a refund of the capture with the repair key), end the order
/// Compensated, record the outcome on the case. The repair keys (…:void:case-{id}) differ from the original ones, so a
/// repair never repeats an original effect, and compensation by key also prohibits a late original. Other kinds are
/// recorded only: what follows a failed delivery is decided outside this workflow.
/// </summary>
public sealed partial class RepairOrderWorkflow : Workflow<RepairInput, RepairResult>
{
    private static readonly WorkflowRetryPolicy Retry = new(5, TimeSpan.FromSeconds(1), 2, TimeSpan.FromSeconds(30));
    private static readonly WorkflowTaskOptions OnOrderApi = new(Retry, AppIds.OrderApi);
    private static readonly WorkflowTaskOptions OnInventory = new(Retry, AppIds.InventoryService);
    private static readonly WorkflowTaskOptions OnPayment = new(Retry, AppIds.PaymentService);

    public override async Task<RepairResult> RunAsync(WorkflowContext context, RepairInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var log = context.CreateReplaySafeLogger<RepairOrderWorkflow>();
        string outcome;
        var status = "unchanged";
        if (input.Kind is InterventionKinds.AmbiguousPayment or InterventionKinds.CompensationExhausted)
        {
            await context.CallActivityAsync<ReleaseHoldsResult>(InventoryActivities.ReleaseOrderHolds, new ReleaseHoldsRequest(input.OrderId, "Repair"), OnInventory);
            var qualifier = PaymentSteps.Repair(input.CaseId);
            var voided = await context.CallActivityAsync<PaymentOperationResult>(PaymentActivities.ExecutePayment,
                new PaymentOperationRequest(input.OrderId, PaymentSteps.Void, qualifier, input.AmountMinor, input.Currency, PaymentSteps.Authorize, PaymentSteps.First, true), OnPayment);
            outcome = "Void:" + voided.Outcome;
            if (voided.Outcome == PaymentOutcomes.Rejected && voided.Code == "AlreadyCaptured")
            {
                var refunded = await context.CallActivityAsync<PaymentOperationResult>(PaymentActivities.ExecutePayment,
                    new PaymentOperationRequest(input.OrderId, PaymentSteps.Refund, qualifier, input.AmountMinor, input.Currency, PaymentSteps.Capture, PaymentSteps.First, true), OnPayment);
                outcome += ",Refund:" + refunded.Outcome;
            }

            var closed = await context.CallActivityAsync<StepResult>(FulfillmentWorkflow.CloseUnfulfilledOrderActivity,
                new CloseUnfulfilledRequest(input.OrderId, "Compensated", "PaymentFailed"), OnOrderApi);
            status = closed.OrderStatus;
        }
        else
        {
            outcome = "RecordedOnly";
        }

        await context.CallActivityAsync<StepResult>(RepairWorkflow.RecordRepairOutcomeActivity, new RepairOutcomeRequest(input.CaseId, outcome), OnOrderApi);
        LogRepaired(log, context.InstanceId, input.Kind, input.Action, outcome);
        return new RepairResult(outcome, status);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "repair {InstanceId} ({Kind}, {Action}): {Outcome}")]
    private static partial void LogRepaired(ILogger logger, string instanceId, string kind, string action, string outcome);
}
