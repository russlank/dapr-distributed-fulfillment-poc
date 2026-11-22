using Fulfillment.Contracts.Events;
using Microsoft.Data.SqlClient;

namespace Fulfillment.Messaging.Outbox;

/// <summary>
/// One source's outbox table (schema-qualified, for example <c>inventory.Outbox</c>). Every owning database has
/// the same columns: Id, EventId, OrderId, Sequence (unique per order), EventType, SchemaVersion, Payload (the
/// event's data as JSON), State (<c>Pending</c> → <c>Published</c>), Attempts, NextAttemptAt, LeaseOwner,
/// LeaseUntil, Generation, Stalled, LastError, CreatedAt, PublishedAt. Rows are never deleted.
/// </summary>
public sealed record OutboxTable(string Source, string Table)
{
    public string Topic => EventSources.TopicOf(Source);
}

/// <summary>Pending rows, pending rows older than the aging threshold, and stalled rows of one outbox.</summary>
public sealed record OutboxHealth(int Pending, int Aging, int Stalled);

public sealed record OutboxRow(long Id, Guid EventId, Guid OrderId, int Sequence, string EventType, int SchemaVersion, string Payload, DateTime CreatedAtUtc,
    int Attempts, int Generation);

/// <summary>Guarded SQL over an outbox table. Every statement is one conditional update; nothing is deleted.</summary>
public sealed class OutboxStore(string connectionString, OutboxTable table)
{
    public OutboxTable Table => table;

    /// <summary>
    /// Appends the order's next event in the caller's transaction (the business transaction): the sequence is the
    /// order's highest plus one, read under an update range lock, so it is gapless and unique even under
    /// concurrent writers; a rollback removes the row with the business change. Returns the sequence.
    /// </summary>
    public static async Task<int> AppendAsync(SqlConnection connection, SqlTransaction tx, string table, Guid orderId, string eventType, string payload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new SqlCommand(
            $"""
            INSERT {table} (EventId, OrderId, Sequence, EventType, Payload)
            OUTPUT inserted.Sequence
            SELECT @id, @o, ISNULL(MAX(Sequence), 0) + 1, @t, @p
            FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @o;
            """, connection, tx);
        command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@t", eventType);
        command.Parameters.AddWithValue("@p", payload);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Leases up to <paramref name="batch"/> due rows: <c>Pending</c>, due, and not leased by a live lease. An expired
    /// lease (a relay that died) is taken over, so two relays may publish the same row; that is allowed.
    /// </summary>
    public async Task<IReadOnlyList<OutboxRow>> ClaimAsync(string owner, int batch, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            WITH due AS (
                SELECT TOP (@n) * FROM {table.Table} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE State = 'Pending' AND NextAttemptAt <= SYSUTCDATETIME() AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME())
                ORDER BY Id)
            UPDATE due SET LeaseOwner = @me, LeaseUntil = DATEADD(millisecond, @lease, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.EventId, inserted.OrderId, inserted.Sequence, inserted.EventType, inserted.SchemaVersion, inserted.Payload,
                   inserted.CreatedAt, inserted.Attempts, inserted.Generation;
            """, connection);
        command.Parameters.AddWithValue("@n", batch);
        command.Parameters.AddWithValue("@me", owner);
        command.Parameters.AddWithValue("@lease", (int)lease.TotalMilliseconds);
        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxRow(reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc), reader.GetInt32(8), reader.GetInt32(9)));
        }

        return rows.OrderBy(r => r.OrderId).ThenBy(r => r.Sequence).ToList();
    }

    /// <summary>
    /// Marks a row published after the broker acknowledged it. Idempotent, and independent of the lease (the
    /// publication happened); conditional on the generation, so a replay requested meanwhile is not swallowed.
    /// </summary>
    public async Task<bool> MarkPublishedAsync(long id, int generation, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            UPDATE {table.Table}
            SET State = 'Published', PublishedAt = SYSUTCDATETIME(), LeaseOwner = NULL, LeaseUntil = NULL, Stalled = 0, LastError = NULL
            WHERE Id = @id AND State = 'Pending' AND Generation = @g;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@g", generation);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Records a failed publication: the row stays <c>Pending</c> with exponential backoff (capped); from
    /// <paramref name="stallThreshold"/> attempts it is flagged <c>Stalled</c> and still retried. Only the lease holder
    /// records it, so a relay whose lease expired cannot postpone a row another relay holds.
    /// </summary>
    public async Task RecordFailureAsync(long id, int generation, string owner, string error, TimeSpan firstBackoff, TimeSpan maxBackoff, int stallThreshold,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            UPDATE {table.Table}
            SET Attempts = Attempts + 1,
                NextAttemptAt = DATEADD(millisecond,
                    CASE WHEN @first * POWER(2.0, CASE WHEN Attempts > 20 THEN 20 ELSE Attempts END) > @max THEN @max
                         ELSE @first * POWER(2.0, CASE WHEN Attempts > 20 THEN 20 ELSE Attempts END) END, SYSUTCDATETIME()),
                Stalled = CASE WHEN Attempts + 1 >= @stall THEN 1 ELSE Stalled END,
                LastError = LEFT(@err, 200), LeaseOwner = NULL, LeaseUntil = NULL
            WHERE Id = @id AND State = 'Pending' AND Generation = @g AND LeaseOwner = @me;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@g", generation);
        command.Parameters.AddWithValue("@me", owner);
        command.Parameters.AddWithValue("@err", error);
        command.Parameters.AddWithValue("@first", firstBackoff.TotalMilliseconds);
        command.Parameters.AddWithValue("@max", maxBackoff.TotalMilliseconds);
        command.Parameters.AddWithValue("@stall", stallThreshold);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Publishes the order's rows from <paramref name="fromSequence"/> again: published rows return to <c>Pending</c>
    /// with a new generation, pending rows become due now. Consumers that have them treat them as duplicates.
    /// </summary>
    public async Task<OutboxReplayResult> ReplayAsync(Guid orderId, int fromSequence, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            UPDATE {table.Table}
            SET State = 'Pending', Generation = Generation + 1, NextAttemptAt = SYSUTCDATETIME(), LeaseOwner = NULL, LeaseUntil = NULL
            WHERE OrderId = @o AND Sequence >= @from;
            SELECT @@ROWCOUNT, (SELECT ISNULL(MAX(Sequence), 0) FROM {table.Table} WHERE OrderId = @o);
            """, connection);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@from", Math.Max(1, fromSequence));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new OutboxReplayResult(table.Source, orderId, fromSequence, reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// The highest sequence per order that was published before <paramref name="settledBeforeUtc"/> (so it had time to
    /// arrive), for orders with a row created since <paramref name="sinceUtc"/>.
    /// </summary>
    public async Task<IReadOnlyList<OutboxHead>> HeadsAsync(DateTime sinceUtc, DateTime settledBeforeUtc, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            SELECT OrderId, MAX(CASE WHEN State = 'Published' AND PublishedAt < @settled THEN Sequence ELSE 0 END)
            FROM {table.Table}
            WHERE OrderId IN (SELECT OrderId FROM {table.Table} WHERE CreatedAt >= @since)
            GROUP BY OrderId;
            """, connection);
        command.Parameters.Add("@since", System.Data.SqlDbType.DateTime2).Value = sinceUtc;
        command.Parameters.Add("@settled", System.Data.SqlDbType.DateTime2).Value = settledBeforeUtc;
        var heads = new List<OutboxHead>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            heads.Add(new OutboxHead(reader.GetGuid(0), reader.GetInt32(1)));
        }

        return heads;
    }

    /// <summary>Depth of the outbox: pending rows, pending rows older than <paramref name="aging"/> (by SQL time), and stalled rows.</summary>
    public async Task<OutboxHealth> HealthAsync(TimeSpan aging, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            SELECT COUNT(*),
                   COUNT(CASE WHEN CreatedAt < DATEADD(millisecond, -@ms, SYSUTCDATETIME()) THEN 1 END),
                   COUNT(CASE WHEN Stalled = 1 THEN 1 END)
            FROM {table.Table} WHERE State = 'Pending';
            """, connection);
        command.Parameters.AddWithValue("@ms", (int)aging.TotalMilliseconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new OutboxHealth(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
