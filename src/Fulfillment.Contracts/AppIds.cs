namespace Fulfillment.Contracts;

/// <summary>Dapr app ids of the Dapr-enabled roles. External simulators have none.</summary>
public static class AppIds
{
    public const string OrderApi = "order-api";
    public const string WorkflowHost = "workflow-host";
    public const string ComputeWorker = "compute-worker";
    public const string InventoryService = "inventory-service";
    public const string PaymentService = "payment-service";
    public const string ShippingService = "shipping-service";
}
