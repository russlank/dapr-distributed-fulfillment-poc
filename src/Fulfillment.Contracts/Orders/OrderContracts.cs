namespace Fulfillment.Contracts.Orders;

/// <summary>Public order API routes and headers (HTTP only; the client simulator uses these too).</summary>
public static class OrderRoutes
{
    public const string Orders = "/orders";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ApiKeyHeader = "X-Api-Key";
}

/// <summary>Contract bounds checked at the API; business rules belong to the workflow's validation.</summary>
public static class OrderLimits
{
    public const int MaxLines = 20;
    public const int MinQuantity = 1;
    public const int MaxQuantity = 100;
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxCustomerIdLength = 64;
    public const int MaxSkuLength = 32;
    public const int MaxIdempotencyKeyLength = 100;
}

/// <summary>Stable problem codes of the order API (the "code" extension of a problem response).</summary>
public static class OrderErrorCodes
{
    public const string InvalidJson = "invalid-json";
    public const string BodyTooLarge = "body-too-large";
    public const string ValidationFailed = "validation-failed";
    public const string IdempotencyKeyMissing = "idempotency-key-missing";
    public const string IdempotencyKeyInvalid = "idempotency-key-invalid";
    public const string IdempotencyKeyReused = "idempotency-key-reused";
}

public sealed record SubmitOrderRequest(string CustomerId, IReadOnlyList<SubmitOrderLine> Lines);

public sealed record SubmitOrderLine(string Sku, int Quantity);

/// <summary>
/// Response of an accepted submission. <c>Replayed</c> is true when the same idempotency key and
/// payload were accepted before; the order is then the original one.
/// </summary>
public sealed record OrderAccepted(Guid OrderId, string Status, bool Replayed);

/// <summary>Business view of an order. Workflow execution state is not part of it.</summary>
public sealed record OrderView(
    Guid OrderId,
    string CustomerId,
    string Status,
    DateTime CreatedAt,
    IReadOnlyList<OrderLineView> Lines,
    IReadOnlyList<OrderStatusChange> History);

public sealed record OrderLineView(int LineNumber, string Sku, int Quantity);

public sealed record OrderStatusChange(int Sequence, string? FromStatus, string ToStatus, string ReasonCode, DateTime OccurredAt);
