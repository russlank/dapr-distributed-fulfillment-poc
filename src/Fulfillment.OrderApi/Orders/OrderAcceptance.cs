using Fulfillment.Contracts.Orders;
using Fulfillment.OrderApi.Domain;
using Fulfillment.OrderApi.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Fulfillment.OrderApi.Orders;

public enum AcceptanceOutcome
{
    /// <summary>A new order was committed.</summary>
    Accepted,

    /// <summary>The key was used before with the same payload: the original order.</summary>
    Replayed,

    /// <summary>The key was used before with another payload: nothing was written.</summary>
    KeyReused,
}

public sealed record AcceptanceResult(AcceptanceOutcome Outcome, Guid? OrderId, OrderStatus? Status);

/// <summary>
/// Durable order acceptance. One transaction writes the order, its lines, the first status-history
/// row, the idempotency key with the request hash, and the workflow start request; either all of
/// them exist or none. No workflow is started here: the start dispatcher picks up the Pending start
/// request, so an accepted order survives any crash after the commit.
///
/// Concurrent submissions with one key (any replica) are decided by the key's primary key: the
/// first commit wins; a concurrent insert waits for it and then fails with a unique violation, after
/// which the stored key is read and compared like a sequential retry.
/// </summary>
public sealed partial class OrderAcceptance(OrderDbContext db, ILogger<OrderAcceptance> logger)
{
    public async Task<AcceptanceResult> AcceptAsync(string idempotencyKey, SubmitOrderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = OrderRequests.Hash(request);

        // Fast path for retries: the key exists already.
        var existing = await FindAsync(idempotencyKey, hash, ct);
        if (existing is not null)
        {
            return existing;
        }

        var orderId = Guid.CreateVersion7();
        var order = Order.Submit(orderId, request.CustomerId, request.Lines.Select(l => (l.Sku, l.Quantity)));
        db.Orders.Add(order);
        db.IdempotencyKeys.Add(new IdempotencyKey(idempotencyKey, hash, orderId));
        db.WorkflowStartRequests.Add(WorkflowStartRequest.ForNewOrder(orderId));
        try
        {
            // SaveChanges runs all inserts in one transaction.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsKeyConflict(e))
        {
            db.ChangeTracker.Clear();
            var winner = await FindAsync(idempotencyKey, hash, ct)
                ?? throw new InvalidOperationException("idempotency key conflict reported, but the key does not exist", e);
            Log.ConcurrentKeyResolved(logger, winner.Outcome, winner.OrderId);
            return winner;
        }

        Log.Accepted(logger, orderId, order.Lines.Count);
        return new AcceptanceResult(AcceptanceOutcome.Accepted, orderId, order.Status);
    }

    private async Task<AcceptanceResult?> FindAsync(string idempotencyKey, byte[] hash, CancellationToken ct)
    {
        var stored = await db.IdempotencyKeys.AsNoTracking()
            .Where(k => k.Key == idempotencyKey)
            .Select(k => new { k.RequestHash, k.OrderId })
            .SingleOrDefaultAsync(ct);
        if (stored is null)
        {
            return null;
        }

        if (!stored.RequestHash.AsSpan().SequenceEqual(hash))
        {
            Log.KeyReused(logger, stored.OrderId);
            return new AcceptanceResult(AcceptanceOutcome.KeyReused, null, null);
        }

        var status = await db.Orders.AsNoTracking().Where(o => o.OrderId == stored.OrderId).Select(o => o.Status).SingleAsync(ct);
        return new AcceptanceResult(AcceptanceOutcome.Replayed, stored.OrderId, status);
    }

    private static bool IsKeyConflict(DbUpdateException e) =>
        e.InnerException is SqlException { Number: 2627 or 2601 } sql
        && sql.Message.Contains(OrderDbConstraints.IdempotencyKeyPrimaryKey, StringComparison.Ordinal);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "order {OrderId} accepted with {LineCount} lines; start request pending")]
        public static partial void Accepted(ILogger logger, Guid orderId, int lineCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "idempotency key reused with a different payload (order {OrderId}); rejected")]
        public static partial void KeyReused(ILogger logger, Guid orderId);

        [LoggerMessage(Level = LogLevel.Information, Message = "concurrent submission with one idempotency key resolved as {Outcome} for order {OrderId}")]
        public static partial void ConcurrentKeyResolved(ILogger logger, AcceptanceOutcome outcome, Guid? orderId);
    }
}
