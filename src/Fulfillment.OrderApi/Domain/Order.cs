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

    /// <summary>Rejected by validation: a SKU appears on more than one line.</summary>
    public const string DuplicateSku = "DuplicateSku";
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
