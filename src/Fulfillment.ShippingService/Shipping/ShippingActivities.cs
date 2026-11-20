using Dapr.Workflow;
using Fulfillment.Contracts.Shipping;

namespace Fulfillment.ShippingService.Shipping;

/// <summary>Dispatch by shipment id: safe to repeat (the shipment is recorded once; the carrier answers a repeat with its stored acceptance).</summary>
public sealed class DispatchShipmentActivity(ShippingOperations operations) : WorkflowActivity<DispatchShipmentRequest, DispatchShipmentResult>
{
    public override Task<DispatchShipmentResult> RunAsync(WorkflowActivityContext context, DispatchShipmentRequest input) =>
        operations.DispatchAsync(input, CancellationToken.None);
}

public sealed class GetShipmentStatusActivity(ShippingOperations operations) : WorkflowActivity<ShipmentStatusRequest, ShipmentStatusResult>
{
    public override Task<ShipmentStatusResult> RunAsync(WorkflowActivityContext context, ShipmentStatusRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return operations.StatusAsync(input.OrderId, CancellationToken.None);
    }
}

public sealed class PollCarrierStatusActivity(ShippingOperations operations) : WorkflowActivity<ShipmentStatusRequest, ShipmentStatusResult>
{
    public override Task<ShipmentStatusResult> RunAsync(WorkflowActivityContext context, ShipmentStatusRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return operations.PollAsync(input.OrderId, CancellationToken.None);
    }
}
