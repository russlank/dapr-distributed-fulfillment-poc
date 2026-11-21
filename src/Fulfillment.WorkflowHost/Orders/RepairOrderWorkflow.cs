using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Shipping;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>
/// The repair of a resolved intervention case whose original workflow no longer waits (terminal, failed, or missing).
/// Payment cases (AmbiguousPayment, CompensationExhausted): release the holds, void the authorization's key with the
/// repair key (a void that finds a capture is followed by a refund of the capture with the repair key), end the order
/// Compensated, record the outcome on the case. The repair keys (…:void:case-{id}) differ from the original ones, so a
/// repair never repeats an original effect, and compensation by key also prohibits a late original.
/// Reconciler cases: a failed or vanished workflow confirmed as a failure is compensated the same way (reason
/// WorkflowFailed) while the order has not reached the point of no return; a completed run that left the order
/// awaiting delivery is finalized when Shipping's own record shows the delivery. Everything else is recorded only:
/// what follows a failed delivery, or a failure after dispatch, is decided outside this workflow.
/// </summary>
public sealed partial class RepairOrderWorkflow : Workflow<RepairInput, RepairResult>
{
    private static readonly WorkflowRetryPolicy Retry = new(5, TimeSpan.FromSeconds(1), 2, TimeSpan.FromSeconds(30));
    private static readonly WorkflowTaskOptions OnOrderApi = new(Retry, AppIds.OrderApi);
    private static readonly WorkflowTaskOptions OnInventory = new(Retry, AppIds.InventoryService);
    private static readonly WorkflowTaskOptions OnPayment = new(Retry, AppIds.PaymentService);
    private static readonly WorkflowTaskOptions OnShipping = new(Retry, AppIds.ShippingService);

    /// <summary>Statuses before the point of no return in which compensation is still allowed.</summary>
    private static readonly string[] Compensable = ["Submitted", "Validating", "AwaitingApproval", "Reserving", "PaymentAuthorized", "Packing", "ManualInterventionRequired"];

    public override async Task<RepairResult> RunAsync(WorkflowContext context, RepairInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var log = context.CreateReplaySafeLogger<RepairOrderWorkflow>();
        string outcome;
        var status = "unchanged";
        var workflowCase = input.Kind is InterventionKinds.WorkflowFaulted or InterventionKinds.WorkflowMissing or InterventionKinds.ExecutionMismatch;
        if (input.Kind is InterventionKinds.ExecutionMismatch && input.Action == CaseActions.ConfirmExternalSuccess && input.OrderStatus == "AwaitingDelivery")
        {
            var shipment = await context.CallActivityAsync<ShipmentStatusResult>(ShippingActivities.GetShipmentStatus, new ShipmentStatusRequest(input.OrderId), OnShipping);
            if (shipment.TerminalStatus == "Delivered")
            {
                var finalized = await context.CallActivityAsync<StepResult>(OrderLifecycleActivities.FinalizeDelivered,
                    new CompleteFulfillmentRequest(input.OrderId, context.InstanceId), OnOrderApi);
                outcome = "Finalized:" + finalized.Outcome;
                status = finalized.OrderStatus;
            }
            else
            {
                outcome = "NotDelivered:" + (shipment.TerminalStatus ?? shipment.CurrentStatus ?? "none");
            }
        }
        else if (input.Kind is InterventionKinds.AmbiguousPayment or InterventionKinds.CompensationExhausted
                 || (workflowCase && input.Action == CaseActions.ConfirmExternalFailure && Compensable.Contains(input.OrderStatus)))
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
                new CloseUnfulfilledRequest(input.OrderId, "Compensated", workflowCase ? "WorkflowFailed" : "PaymentFailed"), OnOrderApi);
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
