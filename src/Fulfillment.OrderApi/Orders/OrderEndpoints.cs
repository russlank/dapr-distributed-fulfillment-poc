using Fulfillment.Contracts.Orders;
using Fulfillment.OrderApi.Persistence;
using Fulfillment.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Fulfillment.OrderApi.Orders;

/// <summary>Public order API (customer role key). Thin: parsing and mapping here, acceptance in <see cref="OrderAcceptance"/>.</summary>
public static class OrderEndpoints
{
    public const string ApiKeyVariable = "CUSTOMER_API_KEY";

    public static void MapOrders(this WebApplication app)
    {
        var orders = app.MapGroup(OrderRoutes.Orders).RequireApiKey(ApiKeyVariable);
        orders.MapPost("/", SubmitAsync);
        orders.MapGet("/{orderId:guid}", GetAsync);
        orders.MapPost("/{orderId:guid}/cancel", CancelAsync);
    }

    /// <summary>
    /// Customer cancellation: 202 recorded (the order ends Cancelled at the fulfillment's next check), 200 for a repeat
    /// or an order already Cancelled, 409 once the dispatch has started or the order is otherwise terminal, 404.
    /// </summary>
    private static async Task<IResult> CancelAsync(Guid orderId, CancellationSteps cancellation, CancellationToken ct) =>
        await cancellation.RequestAsync(orderId, ct) switch
        {
            CancelOutcome.Recorded => Results.Accepted(value: new { orderId, cancellation = "Requested" }),
            CancelOutcome.AlreadyRequested => Results.Ok(new { orderId, cancellation = "Requested" }),
            CancelOutcome.DispatchStarted => Problem(409, OrderErrorCodes.DispatchStarted, "the dispatch has started; the order can no longer be cancelled"),
            CancelOutcome.OrderTerminal => Problem(409, OrderErrorCodes.OrderTerminal, "the order is already finished"),
            _ => Problem(404, OrderErrorCodes.OrderNotFound, "no such order"),
        };

    private static async Task<IResult> SubmitAsync(HttpRequest http, OrderAcceptance acceptance, CancellationToken ct)
    {
        var key = http.Headers[OrderRoutes.IdempotencyKeyHeader].ToString();
        if (string.IsNullOrEmpty(key))
        {
            return Problem(400, OrderErrorCodes.IdempotencyKeyMissing, $"the {OrderRoutes.IdempotencyKeyHeader} header is required");
        }

        if (!OrderRequests.IsValidIdempotencyKey(key))
        {
            return Problem(400, OrderErrorCodes.IdempotencyKeyInvalid, $"{OrderRoutes.IdempotencyKeyHeader}: 1-{OrderLimits.MaxIdempotencyKeyLength} visible ASCII characters");
        }

        var body = await ReadBodyAsync(http, ct);
        if (body is null)
        {
            return Problem(413, OrderErrorCodes.BodyTooLarge, $"the body exceeds {OrderLimits.MaxBodyBytes} bytes");
        }

        var request = OrderRequests.TryParse(body, out var errorPath);
        if (request is null)
        {
            return Problem(400, OrderErrorCodes.InvalidJson, $"the body is not a valid order document (at {errorPath})");
        }

        var errors = OrderRequests.Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "the order violates the contract",
                extensions: new Dictionary<string, object?> { ["code"] = OrderErrorCodes.ValidationFailed });
        }

        var result = await acceptance.AcceptAsync(key, request, ct);
        return result.Outcome switch
        {
            AcceptanceOutcome.Accepted => Results.Created($"{OrderRoutes.Orders}/{result.OrderId}",
                new OrderAccepted(result.OrderId!.Value, result.Status!.Value.ToString(), Replayed: false)),
            AcceptanceOutcome.Replayed => Results.Ok(new OrderAccepted(result.OrderId!.Value, result.Status!.Value.ToString(), Replayed: true)),
            _ => Problem(422, OrderErrorCodes.IdempotencyKeyReused, "the idempotency key was used with a different request"),
        };
    }

    private static async Task<IResult> GetAsync(Guid orderId, OrderDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.History)
            .AsSplitQuery().SingleOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new OrderView(
            order.OrderId,
            order.CustomerId,
            order.Status.ToString(),
            DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc),
            order.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineView(l.LineNumber, l.Sku, l.Quantity)).ToList(),
            order.History.OrderBy(h => h.Sequence)
                .Select(h => new OrderStatusChange(h.Sequence, h.FromStatus?.ToString(), h.ToStatus.ToString(), h.ReasonCode,
                    DateTime.SpecifyKind(h.OccurredAt, DateTimeKind.Utc)))
                .ToList()));
    }

    /// <summary>Reads at most the size limit; null when the body is larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest http, CancellationToken ct)
    {
        if (http.ContentLength > OrderLimits.MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > OrderLimits.MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
