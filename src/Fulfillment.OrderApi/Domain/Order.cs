namespace Fulfillment.OrderApi.Domain;

/// <summary>Business status of an order in OrderDb. Workflow runtime status is never business truth.</summary>
public enum OrderStatus
{
    Submitted,
    Validating,
    Reserving,
    PaymentAuthorized,
    Packing,
    AwaitingApproval,
    Dispatched,
    AwaitingDelivery,
    Completed,
    Rejected,
    Cancelled,
    Compensated,
    ManualInterventionRequired,
}

public static class OrderStatuses
{
    /// <summary>Terminal for the fulfillment execution: a terminal order is never started or run again.</summary>
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Completed or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Compensated;
}

/// <summary>Reason codes of status changes (closed code domain).</summary>
public static class OrderReasonCodes
{
    public const string Accepted = "Accepted";

    /// <summary>The fulfillment run began validating the order.</summary>
    public const string ValidationStarted = "ValidationStarted";

    /// <summary>The simulated fulfillment of M1 completed the order (no real reservation, payment, or shipment).</summary>
    public const string StubFulfillmentCompleted = "StubFulfillmentCompleted";

    /// <summary>Rejected by validation: the order has no lines (defensive; the API refuses such requests).</summary>
    public const string NoLines = "NoLines";

    /// <summary>
    /// Rejected by validation: a SKU appears on more than one line. A deliberate limitation of this
    /// PoC's order model (one line per SKU keeps reservations and allocations per SKU simple), not a
    /// requirement of the runtime or the inventory design.
    /// </summary>
    public const string DuplicateSku = "DuplicateSku";

    /// <summary>Validating → Reserving: the stored risk check requires no review.</summary>
    public const string NoReviewRequired = "NoReviewRequired";

    /// <summary>Validating → AwaitingApproval: the stored risk check requires a review.</summary>
    public const string ReviewRequired = "ReviewRequired";

    /// <summary>AwaitingApproval → Reserving: the review was approved (decision in SQL).</summary>
    public const string ReviewApproved = "ReviewApproved";

    /// <summary>AwaitingApproval → Rejected: the review was rejected (decision in SQL).</summary>
    public const string ReviewRejected = "ReviewRejected";

    /// <summary>AwaitingApproval → Rejected: the last review round expired without a decision.</summary>
    public const string ReviewExpired = "ReviewExpired";

    /// <summary>Rejected after reservation began: no warehouse could serve a line whole; holds released.</summary>
    public const string InsufficientStock = "InsufficientStock";

    /// <summary>Compensated: a hold expired (by SQL time) before it was consumed; holds released.</summary>
    public const string ReservationExpired = "ReservationExpired";

    /// <summary>Compensated: a reservation or consume step failed after its retries; holds released.</summary>
    public const string ReservationFailed = "ReservationFailed";

    /// <summary>Reserving → PaymentAuthorized: the provider authorized the order's amount.</summary>
    public const string PaymentAuthorized = "PaymentAuthorized";

    /// <summary>PaymentAuthorized → Packing.</summary>
    public const string PackingStarted = "PackingStarted";

    /// <summary>Rejected: the provider declined the authorization; holds released.</summary>
    public const string PaymentDeclined = "PaymentDeclined";

    /// <summary>Compensated: the provider could not be reached and nothing can have been committed; holds released, authorization key voided.</summary>
    public const string PaymentProviderUnavailable = "PaymentProviderUnavailable";

    /// <summary>Compensated: the capture was refused (for example the authorization expired); authorization voided, holds released.</summary>
    public const string PaymentCaptureFailed = "PaymentCaptureFailed";

    /// <summary>Compensated after an intervention case confirmed that the payment did not happen, or a case resolution ended the order.</summary>
    public const string PaymentFailed = "PaymentFailed";

    /// <summary>A non-terminal order waits for an intervention case.</summary>
    public const string InterventionRequired = "InterventionRequired";

    /// <summary>A case on a terminal order: history only, the status stays.</summary>
    public const string PostTerminalCase = "PostTerminalCase";

    /// <summary>ManualInterventionRequired → the status before the case: the case was resolved and the run continues.</summary>
    public const string InterventionResolved = "InterventionResolved";

    /// <summary>Cancelled: the customer's cancellation was recorded before dispatch started; holds released, authorization voided.</summary>
    public const string CancellationRequested = "CancellationRequested";

    /// <summary>Packing → Dispatched: the carrier accepted the shipment.</summary>
    public const string ShipmentDispatched = "ShipmentDispatched";

    /// <summary>Dispatched → AwaitingDelivery.</summary>
    public const string DeliveryAwaited = "DeliveryAwaited";

    /// <summary>AwaitingDelivery → Completed: Shipping recorded the delivery.</summary>
    public const string Delivered = "Delivered";

    /// <summary>A terminal carrier status recorded after the order was already terminal (history only).</summary>
    public const string CarrierStatusAfterTerminal = "CarrierStatusAfterTerminal";

    /// <summary>The fulfillment completed (payment captured, stock consumed).</summary>
    public const string FulfillmentCompleted = "FulfillmentCompleted";
}

/// <summary>
/// An accepted order: the business request and its business status. How far its workflow has got
/// is kept apart, in <see cref="WorkflowStartRequest"/>.
/// </summary>
public sealed class Order
{
    private readonly List<OrderLine> lines = [];
    private readonly List<OrderStatusHistory> history = [];

    private Order()
    {
        CustomerId = null!;
    }

    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>Database time (SYSUTCDATETIME) of the acceptance.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Database time a cancellation was accepted; set only while dispatch has not started (I15).</summary>
    public DateTime? CancelRequestedAt { get; private set; }

    /// <summary>Database time the dispatch started (the cancellation point of no return); set only without a cancellation (I15).</summary>
    public DateTime? DispatchStartedAt { get; private set; }

    /// <summary>Concurrency token for later conditional status transitions.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<OrderLine> Lines => lines;

    public IReadOnlyList<OrderStatusHistory> History => history;

    /// <summary>
    /// Creates a submitted order with its lines (numbered from 1 in request order) and the first
    /// status-history row. The caller persists it together with its idempotency key and start request.
    /// </summary>
    public static Order Submit(Guid orderId, string customerId, IEnumerable<(string Sku, int Quantity)> requestLines)
    {
        ArgumentNullException.ThrowIfNull(requestLines);
        var order = new Order { OrderId = orderId, CustomerId = customerId, Status = OrderStatus.Submitted };
        var number = 0;
        foreach (var (sku, quantity) in requestLines)
        {
            order.lines.Add(new OrderLine(orderId, ++number, sku, quantity));
        }

        order.history.Add(new OrderStatusHistory(orderId, 1, null, OrderStatus.Submitted, OrderReasonCodes.Accepted));
        return order;
    }
}

public sealed class OrderLine
{
    internal OrderLine(Guid orderId, int lineNumber, string sku, int quantity)
    {
        OrderId = orderId;
        LineNumber = lineNumber;
        Sku = sku;
        Quantity = quantity;
    }

    public Guid OrderId { get; private set; }

    /// <summary>1-based position in the request; operation keys use it as qualifier (line-N).</summary>
    public int LineNumber { get; private set; }

    public string Sku { get; private set; }

    public int Quantity { get; private set; }
}

/// <summary>One row per status change, numbered per order; the order's status is the last row.</summary>
public sealed class OrderStatusHistory
{
    internal OrderStatusHistory(Guid orderId, int sequence, OrderStatus? fromStatus, OrderStatus toStatus, string reasonCode)
    {
        OrderId = orderId;
        Sequence = sequence;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ReasonCode = reasonCode;
    }

    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public int Sequence { get; private set; }

    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    public string ReasonCode { get; private set; }

    public DateTime OccurredAt { get; private set; }
}

/// <summary>
/// Client idempotency key bound to the hash of the normalized request. The same key with the same
/// hash returns the original order; with another hash it is a conflict.
/// </summary>
public sealed class IdempotencyKey
{
    public IdempotencyKey(string key, byte[] requestHash, Guid orderId)
    {
        Key = key;
        RequestHash = requestHash;
        OrderId = orderId;
    }

    public string Key { get; private set; }

    /// <summary>SHA-256 of the normalized request (32 bytes).</summary>
    public byte[] RequestHash { get; private set; }

    public Guid OrderId { get; private set; }

    public DateTime CreatedAt { get; private set; }
}
