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

    /// <summary>Extends all holds of the order once, all or none, before the payment capture (bounded by the authorization's expiry).</summary>
    public const string ExtendOrderHolds = "ExtendOrderHolds";
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

    /// <summary>Every hold of the order now lasts at least until the returned time.</summary>
    public const string Extended = "Extended";

    /// <summary>Even extended, the holds would not last the required minimum (for example the authorization expires sooner): nothing changed.</summary>
    public const string InsufficientValidity = "InsufficientValidity";
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

/// <param name="Extension">How much longer than now the holds should last (30 min; drill 30 s).</param>
/// <param name="AuthorizationExpiresAtUtc">The holds are never extended beyond the payment authorization's expiry.</param>
/// <param name="MinimumRemaining">The validity the holds must have after the extension for the capture to start.</param>
public sealed record ExtendHoldsRequest(Guid OrderId, int ExpectedLines, TimeSpan Extension, DateTime? AuthorizationExpiresAtUtc, TimeSpan MinimumRemaining);

/// <param name="ExpiresAtUtc">The earliest expiry among the order's held holds now (SQL time).</param>
/// <param name="RemainingMilliseconds">Validity left at the time of the answer (a replay answers with the current value).</param>
public sealed record ExtendHoldsResult(string Outcome, DateTime? ExpiresAtUtc, long RemainingMilliseconds, bool Replayed, string Replica);
