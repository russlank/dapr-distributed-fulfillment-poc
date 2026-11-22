using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Shipping;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

public sealed partial class FulfillOrderWorkflow
{
    private static readonly WorkflowTaskOptions OnShipping = new(Retry, AppIds.ShippingService);

    /// <summary>
    /// After the holds are consumed: dispatch to the carrier (shipment id derived from the order, so every retry asks
    /// with the same key), Dispatched, AwaitingDelivery, then wait for the carrier's terminal status: woken by the
    /// carrier signal (sent only for a status Shipping applied as terminal) or by the delivery deadline, and every
    /// time Shipping's own record is read, never the signal. Delivered finalizes the order. Exception, an overdue
    /// delivery (after a poll), or a dispatch whose outcome cannot be determined opens an intervention case.
    /// </summary>
    private static async Task<FulfillmentResult> ShipAndDeliverAsync(WorkflowContext context, ILogger log, Guid orderId, ExecutionClaimResult claim, int lines)
    {
        context.SetCustomStatus("dispatching");
        var dispatch = await context.CallActivityAsync<DispatchShipmentResult>(ShippingActivities.DispatchShipment, new DispatchShipmentRequest(orderId, lines), OnShipping);
        for (var round = 1; dispatch.Result != DispatchResults.Dispatched; round++)
        {
            // The stock is consumed and the payment captured: no compensation after this point, an operator decides.
            var resolved = await WaitForCaseAsync(context, orderId, InterventionKinds.AmbiguousDispatch, $"dispatch-{round}",
                $"shipment {dispatch.ShipmentId}: {dispatch.Result} after {dispatch.Attempts} request(s)");
            if (resolved.OrderTerminal)
            {
                return Fenced(log, context, resolved.OrderStatus, claim);
            }

            dispatch = await context.CallActivityAsync<DispatchShipmentResult>(ShippingActivities.DispatchShipment, new DispatchShipmentRequest(orderId, lines), OnShipping);
        }

        foreach (var to in new[] { "Dispatched", "AwaitingDelivery" })
        {
            if (await AdvanceAsync(context, orderId, to) is { } stopped)
            {
                return Fenced(log, context, stopped, claim);
            }
        }

        var polled = false;
        for (var round = 1; ; round++)
        {
            context.SetCustomStatus("awaiting-delivery");
            var status = await context.CallActivityAsync<ShipmentStatusResult>(
                polled ? ShippingActivities.PollCarrierStatus : ShippingActivities.GetShipmentStatus, new ShipmentStatusRequest(orderId), OnShipping);
            if (status.TerminalStatus == CarrierStatuses.Delivered)
            {
                return await FinalizeAsync(context, log, orderId, claim);
            }

            string? caseKind = null;
            if (status.TerminalStatus == CarrierStatuses.Exception)
            {
                caseKind = InterventionKinds.DeliveryException;
            }
            else if (status.DeadlinePassed && polled)
            {
                caseKind = InterventionKinds.DeliveryOverdue;
            }
            else if (status.DeadlinePassed)
            {
                // Past the deadline without a terminal status: ask the carrier once, then decide.
                polled = true;
                continue;
            }

            if (caseKind is not null)
            {
                var resolved = await WaitForCaseAsync(context, orderId, caseKind, $"delivery-{round}",
                    $"shipment {status.ShipmentId}: current {status.CurrentStatus}, terminal {status.TerminalStatus}, deadline {status.DeliveryDeadlineAtUtc:O}");
                if (resolved.OrderTerminal)
                {
                    return Fenced(log, context, resolved.OrderStatus, claim);
                }

                if (resolved.Action == CaseActions.ConfirmExternalSuccess)
                {
                    return await FinalizeAsync(context, log, orderId, claim);
                }

                // A confirmed failure escalates the order (terminal) in the resolution's transaction, so the fence above
                // ends the run; this branch only remains for a resolution recorded before escalation existed.
                context.SetCustomStatus($"delivery-unresolved:{caseKind}");
                return new FulfillmentResult(FulfillmentOutcomes.Fenced, resolved.OrderStatus, claim.ExecutionClaims);
            }

            using var cancel = new CancellationTokenSource();
            var signal = context.WaitForExternalEventAsync<object>(CarrierSignals.EventName(status.ShipmentId), cancel.Token);
            var deadline = status.DeliveryDeadlineAtUtc ?? context.CurrentUtcDateTime;
            var earliest = context.CurrentUtcDateTime + TimeSpan.FromSeconds(1);
            var timer = context.CreateTimer(deadline > earliest ? deadline : earliest, cancel.Token);
            await Task.WhenAny(signal, timer);
#pragma warning disable CA1849 // workflow code awaits only workflow tasks
            cancel.Cancel();
#pragma warning restore CA1849
        }
    }

    private static async Task<FulfillmentResult> FinalizeAsync(WorkflowContext context, ILogger log, Guid orderId, ExecutionClaimResult claim)
    {
        var finalized = await context.CallActivityAsync<StepResult>(OrderLifecycleActivities.FinalizeDelivered,
            new CompleteFulfillmentRequest(orderId, context.InstanceId), OnOrderApi);
        if (finalized.Outcome == StepOutcomes.AlreadyTerminal)
        {
            return Fenced(log, context, finalized.OrderStatus, claim);
        }

        context.SetCustomStatus("completed");
        return new FulfillmentResult(FulfillmentOutcomes.Completed, "Completed", claim.ExecutionClaims);
    }
}
