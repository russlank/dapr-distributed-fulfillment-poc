namespace Fulfillment.Contracts.Workflows;

/// <summary>
/// Identity of the fulfillment workflow. The instance id is derived from the order id alone (no
/// version suffix), so every component computes the same id and an order has exactly one.
/// </summary>
public static class FulfillmentWorkflow
{
    public const string Name = "FulfillOrderWorkflow";

    public const string InstancePrefix = "fulfill-";

    public static string InstanceId(Guid orderId) => InstancePrefix + orderId.ToString("N");
}
