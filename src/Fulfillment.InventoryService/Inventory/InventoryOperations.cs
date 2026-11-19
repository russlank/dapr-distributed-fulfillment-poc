using System.Text.Json;
using Fulfillment.Contracts.Workflows;
using Microsoft.Data.SqlClient;

namespace Fulfillment.InventoryService.Inventory;

/// <summary>Reserve the whole line quantity in one warehouse.</summary>
/// <param name="OperationKey">Stable key of this logical request (line and warehouse); a repeat returns the stored result.</param>
/// <param name="Ttl">How long the hold stays valid, measured by SQL time.</param>
public sealed record ReserveCommand(Guid OrderId, int LineNumber, string Sku, int Quantity, string WarehouseId, string OperationKey, TimeSpan Ttl);

public sealed record ReserveResult(string Outcome, Guid? HoldId, string WarehouseId, DateTime? ExpiresAtUtc, bool Replayed);

public sealed record ReleaseCommand(Guid OrderId, Guid HoldId, string OperationKey);

public sealed record ReleaseResult(string Outcome, Guid HoldId, string? HoldState, bool Replayed);

public sealed record ConsumeResult(string Outcome, int Consumed, bool Replayed);

public sealed record HoldRow(Guid HoldId, int LineNumber, string WarehouseId, string Sku, int Quantity, string State);

/// <summary>
/// The authoritative inventory operations: guarded SQL, one transaction each, with its
/// ProcessedOperation row (the key's stored result) and an outbox row per hold transition.
///
/// Guarantees do not depend on the actor: Reserve changes <c>Reserved</c> only when
/// <c>OnHand - Reserved &gt;= q</c> (and the check constraint backs it); every hold transition is
/// conditional on <c>State = 'Held'</c>, so a hold leaves Held once (I16); a repeated operation key
/// returns the first result without a second effect (I2); a closed order (released or consumed)
/// refuses any later reserve.
///
/// Lock order, to avoid deadlocks: an exclusive application lock on the order first (every
/// operation of one order, including repeats of one key, runs one at a time; no key-range locks on
/// shared index gaps are needed), then hold rows, then stock rows (ascending warehouse and SKU).
/// Range locks on missing keys were the first design and deadlocked under contention: different
/// orders' gap locks met on the hot stock row (journal).
/// </summary>
public sealed class InventoryOperations(string connectionString, string replica)
{
    private const int DeadlockRetries = 5;

    public string Replica { get; } = replica;

    public Task<ReserveResult> ReserveAsync(ReserveCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.Quantity);
        return WithDeadlockRetryAsync(async () =>
        {
            await using var connection = await OpenAsync(ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await LockOrderAsync(connection, tx, command.OrderId, ct);
            var stored = await StoredAsync(connection, tx, command.OperationKey, ct);
            if (stored is { } s)
            {
                await tx.CommitAsync(ct);
                return new ReserveResult(s.Outcome, s.HoldId, command.WarehouseId, s.HoldId is null ? null : await ExpiresAsync(connection, s.HoldId.Value, ct), true);
            }

            if (await IsClosedAsync(connection, tx, command.OrderId, ct))
            {
                await RecordAsync(connection, tx, command.OperationKey, "Reserve", command.OrderId, InventoryOutcomes.OrderClosed, null, command.WarehouseId, ct);
                await tx.CommitAsync(ct);
                return new ReserveResult(InventoryOutcomes.OrderClosed, null, command.WarehouseId, null, false);
            }

            await using (var take = new SqlCommand(
                             """
                             UPDATE inventory.StockItem SET Reserved = Reserved + @q
                             WHERE WarehouseId = @w AND Sku = @s AND OnHand - Reserved >= @q;
                             """, connection, tx))
            {
                take.Parameters.AddWithValue("@q", command.Quantity);
                take.Parameters.AddWithValue("@w", command.WarehouseId);
                take.Parameters.AddWithValue("@s", command.Sku);
                if (await take.ExecuteNonQueryAsync(ct) != 1)
                {
                    await RecordAsync(connection, tx, command.OperationKey, "Reserve", command.OrderId, InventoryOutcomes.InsufficientStock, null, command.WarehouseId, ct);
                    await tx.CommitAsync(ct);
                    return new ReserveResult(InventoryOutcomes.InsufficientStock, null, command.WarehouseId, null, false);
                }
            }

            Guid holdId;
            DateTime expiresAt;
            await using (var insert = new SqlCommand(
                             """
                             INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy)
                             OUTPUT inserted.HoldId, inserted.ExpiresAt
                             VALUES (@o, @l, @w, @s, @q, 'Held', DATEADD(millisecond, @ttl, SYSUTCDATETIME()), @r);
                             """, connection, tx))
            {
                insert.Parameters.AddWithValue("@o", command.OrderId);
                insert.Parameters.AddWithValue("@l", command.LineNumber);
                insert.Parameters.AddWithValue("@w", command.WarehouseId);
                insert.Parameters.AddWithValue("@s", command.Sku);
                insert.Parameters.AddWithValue("@q", command.Quantity);
                insert.Parameters.AddWithValue("@ttl", (int)command.Ttl.TotalMilliseconds);
                insert.Parameters.AddWithValue("@r", Replica);
                await using var reader = await insert.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                (holdId, expiresAt) = (reader.GetGuid(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
            }

            await OutboxAsync(connection, tx, command.OrderId, "HoldCreated", holdId,
                new { holdId, command.OrderId, command.LineNumber, command.WarehouseId, command.Sku, command.Quantity, expiresAt }, ct);
            await RecordAsync(connection, tx, command.OperationKey, "Reserve", command.OrderId, InventoryOutcomes.Reserved, holdId, command.WarehouseId, ct);
            await tx.CommitAsync(ct);
            return new ReserveResult(InventoryOutcomes.Reserved, holdId, command.WarehouseId, expiresAt, false);
        });
    }

    /// <summary>Releases one hold if it is still Held (Held → Released, Reserved -= q).</summary>
    public Task<ReleaseResult> ReleaseAsync(ReleaseCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WithDeadlockRetryAsync(async () =>
        {
            await using var connection = await OpenAsync(ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await LockOrderAsync(connection, tx, command.OrderId, ct);
            var stored = await StoredAsync(connection, tx, command.OperationKey, ct);
            if (stored is { } s)
            {
                await tx.CommitAsync(ct);
                return new ReleaseResult(s.Outcome, command.HoldId, null, true);
            }

            var (changed, state) = await CloseHoldAsync(connection, tx, command.HoldId, HoldStates.Released, command.OperationKey, null, ct);
            await RecordAsync(connection, tx, command.OperationKey, "Release", command.OrderId, changed ? InventoryOutcomes.Released : InventoryOutcomes.NotHeld, command.HoldId, null, ct);
            await tx.CommitAsync(ct);
            return new ReleaseResult(changed ? InventoryOutcomes.Released : InventoryOutcomes.NotHeld, command.HoldId, state, false);
        });
    }

    /// <summary>
    /// Closes the order for reservations (first, so a reserve that commits later sees it) and lists
    /// its holds that are still Held; the caller releases each one (through its stock actor).
    /// </summary>
    public async Task<IReadOnlyList<HoldRow>> CloseOrderAsync(Guid orderId, string reason, string operationKey, CancellationToken ct = default)
    {
        await WithDeadlockRetryAsync(async () =>
        {
            await using var connection = await OpenAsync(ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await LockOrderAsync(connection, tx, orderId, ct);
            await using var command = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM inventory.OrderClosure WHERE OrderId = @o)
                    INSERT inventory.OrderClosure (OrderId, Reason, OperationKey) VALUES (@o, @r, @k);
                """, connection, tx);
            command.Parameters.AddWithValue("@o", orderId);
            command.Parameters.AddWithValue("@r", reason);
            command.Parameters.AddWithValue("@k", operationKey);
            await command.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return 0;
        });
        return await HoldsAsync(orderId, HoldStates.Held, ct);
    }

    /// <summary>
    /// Consumes every hold of the order in one transaction, or none: all must be Held and unexpired
    /// by SQL time, and there must be <paramref name="expectedLines"/> of them. Consumed holds
    /// decrement OnHand and Reserved; the order is closed for reservations.
    /// </summary>
    public Task<ConsumeResult> ConsumeOrderAsync(Guid orderId, int expectedLines, string operationKey, CancellationToken ct = default) =>
        WithDeadlockRetryAsync(async () =>
        {
            await using var connection = await OpenAsync(ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await LockOrderAsync(connection, tx, orderId, ct);
            var stored = await StoredAsync(connection, tx, operationKey, ct);
            if (stored is { } s)
            {
                await tx.CommitAsync(ct);
                return new ConsumeResult(s.Outcome, s.Outcome == InventoryOutcomes.Consumed ? expectedLines : 0, true);
            }

            await using (var close = new SqlCommand(
                             """
                             IF NOT EXISTS (SELECT 1 FROM inventory.OrderClosure WHERE OrderId = @o)
                                 INSERT inventory.OrderClosure (OrderId, Reason, OperationKey) VALUES (@o, 'Consumed', @k);
                             """, connection, tx))
            {
                close.Parameters.AddWithValue("@o", orderId);
                close.Parameters.AddWithValue("@k", operationKey);
                await close.ExecuteNonQueryAsync(ct);
            }

            var holds = new List<(Guid Id, string Warehouse, string Sku, int Quantity, bool Usable)>();
            await using (var read = new SqlCommand(
                             """
                             SELECT HoldId, WarehouseId, Sku, Quantity,
                                    CASE WHEN State = 'Held' AND ExpiresAt > SYSUTCDATETIME() THEN 1 ELSE 0 END
                             FROM inventory.Hold WITH (UPDLOCK, ROWLOCK)
                             WHERE OrderId = @o ORDER BY WarehouseId, Sku;
                             """, connection, tx))
            {
                read.Parameters.AddWithValue("@o", orderId);
                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    holds.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4) == 1));
                }
            }

            var outcome = holds.Count != expectedLines ? InventoryOutcomes.Incomplete
                : holds.Any(h => !h.Usable) ? InventoryOutcomes.HoldExpired
                : InventoryOutcomes.Consumed;
            if (outcome == InventoryOutcomes.Consumed)
            {
                foreach (var hold in holds)
                {
                    var (changed, _) = await CloseHoldAsync(connection, tx, hold.Id, HoldStates.Consumed, operationKey, orderId, ct);
                    if (!changed)
                    {
                        throw new InvalidOperationException($"hold {hold.Id} changed while it was locked");
                    }
                }
            }
            else
            {
                // Nothing consumed: the closure taken above must not stay (the caller releases, which closes again).
                await using var reopen = new SqlCommand("DELETE inventory.OrderClosure WHERE OrderId = @o AND OperationKey = @k;", connection, tx);
                reopen.Parameters.AddWithValue("@o", orderId);
                reopen.Parameters.AddWithValue("@k", operationKey);
                await reopen.ExecuteNonQueryAsync(ct);
            }

            await RecordAsync(connection, tx, operationKey, "Consume", orderId, outcome, null, null, ct);
            await tx.CommitAsync(ct);
            return new ConsumeResult(outcome, outcome == InventoryOutcomes.Consumed ? holds.Count : 0, false);
        });

    /// <summary>
    /// The sweeper's step: expires one hold whose ExpiresAt passed by SQL time (Held → Expired,
    /// Reserved -= q). Rows locked by another transaction are skipped. Returns the hold, or null
    /// when none is due.
    /// </summary>
    public Task<Guid?> ExpireNextDueAsync(CancellationToken ct = default) =>
        WithDeadlockRetryAsync(async () =>
        {
            await using var connection = await OpenAsync(ct);
            // Find a due hold without locking it, take its order's lock (the common lock order), then
            // expire it only if it is still Held and still due.
            Guid? due = null;
            var orderId = Guid.Empty;
            await using (var pick = new SqlCommand(
                             """
                             SELECT TOP (1) HoldId, OrderId FROM inventory.Hold
                             WHERE State = 'Held' AND ExpiresAt <= SYSUTCDATETIME() ORDER BY ExpiresAt;
                             """, connection))
            {
                await using var reader = await pick.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    (due, orderId) = (reader.GetGuid(0), reader.GetGuid(1));
                }
            }

            if (due is null)
            {
                return (Guid?)null;
            }

            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await LockOrderAsync(connection, tx, orderId, ct);
            await using (var check = new SqlCommand(
                             "SELECT COUNT(*) FROM inventory.Hold WHERE HoldId = @h AND State = 'Held' AND ExpiresAt <= SYSUTCDATETIME();", connection, tx))
            {
                check.Parameters.AddWithValue("@h", due.Value);
                if ((int)(await check.ExecuteScalarAsync(ct))! == 0)
                {
                    await tx.CommitAsync(ct);
                    return (Guid?)null;
                }
            }

            var (changed, _) = await CloseHoldAsync(connection, tx, due.Value, HoldStates.Expired, $"sweeper:{Replica}", orderId, ct);
            await tx.CommitAsync(ct);
            return changed ? due : null;
        });

    public async Task<IReadOnlyList<HoldRow>> HoldsAsync(Guid orderId, string? state = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT HoldId, LineNumber, WarehouseId, Sku, Quantity, State FROM inventory.Hold WHERE OrderId = @o AND (@state IS NULL OR State = @state) ORDER BY WarehouseId, Sku;",
            connection);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@state", (object?)state ?? DBNull.Value);
        var rows = new List<HoldRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new HoldRow(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>Warehouse ids in allocation order.</summary>
    public async Task<IReadOnlyList<string>> WarehousesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("SELECT WarehouseId FROM inventory.Warehouse ORDER BY Priority;", connection);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// Takes a hold out of Held (conditional on its state) and moves the stock accordingly, with its
    /// outbox row. False (nothing changed) when the hold already left Held.
    /// </summary>
    private static async Task<(bool Changed, string? State)> CloseHoldAsync(SqlConnection connection, SqlTransaction tx, Guid holdId, string target, string closedBy, Guid? expectedOrder, CancellationToken ct)
    {
        Guid orderId;
        string warehouse, sku;
        int quantity;
        await using (var close = new SqlCommand(
                         """
                         UPDATE inventory.Hold SET State = @t, ClosedAt = SYSUTCDATETIME(), ClosedBy = @by
                         OUTPUT inserted.OrderId, inserted.WarehouseId, inserted.Sku, inserted.Quantity
                         WHERE HoldId = @h AND State = 'Held';
                         """, connection, tx))
        {
            close.Parameters.AddWithValue("@t", target);
            close.Parameters.AddWithValue("@by", closedBy);
            close.Parameters.AddWithValue("@h", holdId);
            await using var reader = await close.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await reader.CloseAsync();
                return (false, await StateAsync(connection, tx, holdId, ct));
            }

            (orderId, warehouse, sku, quantity) = (reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        }

        if (expectedOrder is { } expected && expected != orderId)
        {
            throw new InvalidOperationException($"hold {holdId} belongs to order {orderId}, not {expected}");
        }

        await using (var stock = new SqlCommand(
                         target == HoldStates.Consumed
                             ? "UPDATE inventory.StockItem SET OnHand = OnHand - @q, Reserved = Reserved - @q WHERE WarehouseId = @w AND Sku = @s;"
                             : "UPDATE inventory.StockItem SET Reserved = Reserved - @q WHERE WarehouseId = @w AND Sku = @s;",
                         connection, tx))
        {
            stock.Parameters.AddWithValue("@q", quantity);
            stock.Parameters.AddWithValue("@w", warehouse);
            stock.Parameters.AddWithValue("@s", sku);
            if (await stock.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException($"stock row {warehouse}/{sku} of hold {holdId} is missing");
            }
        }

        var eventType = target switch
        {
            HoldStates.Released => "HoldReleased",
            HoldStates.Expired => "HoldExpired",
            _ => "HoldConsumed",
        };
        await OutboxAsync(connection, tx, orderId, eventType, holdId, new { holdId, orderId, warehouse, sku, quantity, by = closedBy }, ct);
        return (true, target);
    }

    private static async Task<string?> StateAsync(SqlConnection connection, SqlTransaction tx, Guid holdId, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT State FROM inventory.Hold WHERE HoldId = @h;", connection, tx);
        command.Parameters.AddWithValue("@h", holdId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<DateTime?> ExpiresAsync(SqlConnection connection, Guid holdId, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT ExpiresAt FROM inventory.Hold WHERE HoldId = @h;", connection);
        command.Parameters.AddWithValue("@h", holdId);
        return await command.ExecuteScalarAsync(ct) is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }

    /// <summary>
    /// The stored result of an operation key, or null (counting the replay). Called under the
    /// order's lock: a concurrent request with the same key waits for that lock and then reads this
    /// one's committed result instead of applying a second effect.
    /// </summary>
    private static async Task<(string Outcome, Guid? HoldId)?> StoredAsync(SqlConnection connection, SqlTransaction tx, string key, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            UPDATE inventory.ProcessedOperation SET Replays = Replays + 1
            OUTPUT inserted.Outcome, inserted.HoldId
            WHERE OperationKey = @k;
            """, connection, tx);
        command.Parameters.AddWithValue("@k", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)) : null;
    }

    private async Task RecordAsync(SqlConnection connection, SqlTransaction tx, string key, string operation, Guid orderId, string outcome, Guid? holdId, string? warehouse, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            INSERT inventory.ProcessedOperation (OperationKey, Operation, OrderId, Outcome, HoldId, WarehouseId, ProcessedBy, Replays)
            VALUES (@k, @op, @o, @out, @h, @w, @r, 0);
            """, connection, tx);
        command.Parameters.AddWithValue("@k", key);
        command.Parameters.AddWithValue("@op", operation);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@out", outcome);
        command.Parameters.AddWithValue("@h", (object?)holdId ?? DBNull.Value);
        command.Parameters.AddWithValue("@w", (object?)warehouse ?? DBNull.Value);
        command.Parameters.AddWithValue("@r", Replica);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsClosedAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM inventory.OrderClosure WHERE OrderId = @o;", connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        return (int)(await command.ExecuteScalarAsync(ct))! > 0;
    }

    /// <summary>Appends the order's next outbox row; under the order's lock, so the sequence is gapless and unique per order.</summary>
    private static async Task OutboxAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, string eventType, Guid holdId, object payload, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            INSERT inventory.Outbox (OrderId, Sequence, EventType, HoldId, Payload)
            SELECT @o, ISNULL(MAX(Sequence), 0) + 1, @e, @h, @p
            FROM inventory.Outbox WHERE OrderId = @o;
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@e", eventType);
        command.Parameters.AddWithValue("@h", holdId);
        command.Parameters.AddWithValue("@p", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Exclusive, transaction-scoped application lock on the order (released at commit or rollback).
    /// Reads under it see the latest committed rows: the lock is taken before any of them.
    /// </summary>
    private static async Task LockOrderAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand("sp_getapplock", connection, tx) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Resource", $"inventory-order:{orderId:N}");
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Transaction");
        command.Parameters.AddWithValue("@LockTimeout", 30_000);
        var result = command.Parameters.Add("@Result", System.Data.SqlDbType.Int);
        result.Direction = System.Data.ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(ct);
        if ((int)result.Value < 0)
        {
            throw new TimeoutException($"order lock for {orderId} not granted ({result.Value})");
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var options = new SqlCommand("SET XACT_ABORT ON;", connection);
        await options.ExecuteNonQueryAsync(ct);
        return connection;
    }

    /// <summary>A transaction chosen as deadlock victim rolled back completely; it is safe to run again.</summary>
    private static async Task<T> WithDeadlockRetryAsync<T>(Func<Task<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqlException e) when (e.Number == 1205 && attempt < DeadlockRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt));
            }
        }
    }
}
