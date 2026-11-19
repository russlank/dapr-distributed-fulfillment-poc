namespace Fulfillment.Contracts.Workflows;

/// <summary>Activities of the inventory service (cross-app activities of the fulfillment workflow).</summary>
public static class InventoryActivities
{
    /// <summary>Reserves one order line whole from the first warehouse (by priority) that can serve it.</summary>
    public const string ReserveOrderLine = "ReserveOrderLine";

    /// <summary>Closes the order for reservations and releases every hold of it that is still held.</summary>
    public const string ReleaseOrderHolds = "ReleaseOrderHolds";

    /// <summary>Consumes all holds of the order in one transaction, or none.</summary>
    public const string ConsumeOrderHolds = "ConsumeOrderHolds";
}

/// <summary>Outcomes of inventory operations (business results, never exceptions).</summary>
public static class InventoryOutcomes
{
    public const string Reserved = "Reserved";
    public const string InsufficientStock = "InsufficientStock";

    /// <summary>The order was closed for reservations (released or consumed) before this request.</summary>
    public const string OrderClosed = "OrderClosed";

    public const string Released = "Released";

    /// <summary>The hold had already left Held (released, expired, or consumed): nothing changed.</summary>
    public const string NotHeld = "NotHeld";

    public const string Consumed = "Consumed";

    /// <summary>At least one hold of the order expired (by SQL time) or left Held: nothing was consumed.</summary>
    public const string HoldExpired = "HoldExpired";

    /// <summary>Fewer held lines than the order has: nothing was consumed.</summary>
    public const string Incomplete = "Incomplete";
}

/// <summary>States of a hold; a hold leaves Held at most once (released, expired, or consumed).</summary>
public static class HoldStates
{
    public const string Held = "Held";
    public const string Released = "Released";
    public const string Expired = "Expired";
    public const string Consumed = "Consumed";
}

public sealed record ReserveLineRequest(Guid OrderId, int LineNumber, string Sku, int Quantity);

/// <param name="Outcome">Reserved, InsufficientStock (no warehouse can serve the whole line), or OrderClosed.</param>
/// <param name="Tried">Warehouses asked, in priority order, with their answers (evidence).</param>
public sealed record ReserveLineResult(string Outcome, Guid? HoldId, string? WarehouseId, DateTime? ExpiresAtUtc, IReadOnlyList<string> Tried, string Replica);

public sealed record ReleaseHoldsRequest(Guid OrderId, string Reason);

public sealed record ReleaseHoldsResult(int Released, int NotHeld, string Replica);

public sealed record ConsumeHoldsRequest(Guid OrderId, int ExpectedLines);

public sealed record ConsumeHoldsResult(string Outcome, int Consumed, string Replica);
