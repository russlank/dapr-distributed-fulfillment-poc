using System.Diagnostics.Metrics;
using Fulfillment.Contracts.Events;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Events;

/// <summary>
/// <paramref name="Repaired"/>: pending successors drained on a duplicate or early delivery. Normally 0: a stream's
/// successors are drained when the event before them is applied; a positive value means a stream had been left
/// behind its own history and this delivery repaired it.
/// </summary>
public sealed record IngestResult(string Disposition, int LastAppliedSequence, int Drained, int Repaired = 0);

/// <summary>
/// The server ended an event transaction (a deadlock victim) without the client being told in time; nothing of it
/// was committed. Transient: the broker redelivers.
/// </summary>
public sealed class EventTransactionLostException(Exception inner) : Exception("the event transaction was ended by the server", inner);

/// <summary>
/// The event consumer (plan 7.8): one transaction per event, serialized per (order, source) by locking that
/// stream's current-state row first. Inside it the event is classified with the pure rules; a new event is
/// stored in history (Applied when it is the next sequence, else Pending), applied, and the pending
/// successors are drained in sequence order; a duplicate is counted; anything invalid or conflicting is
/// recorded in the dead-letter table (ids and reason only). The answer to the broker is success in every
/// one of these cases; only a failure (for example SQL unavailable) asks for redelivery.
/// </summary>
public sealed class EventConsumer(string connectionString)
{
    private static readonly Meter Meter = new("Fulfillment.Events");
    private static readonly Counter<long> DispositionCounter = Meter.CreateCounter<long>("event_history_dispositions_total");
    private static readonly Counter<long> RepairCounter = Meter.CreateCounter<long>("event_stream_repairs_total",
        description: "pending events drained on a duplicate or early delivery (a stream had been left behind its history)");

    /// <summary>
    /// Prefixed to every statement sent inside a consumer transaction. When SQL Server picks a statement as a
    /// deadlock victim it rolls the transaction back; SqlClient does not always raise that error (seen with the
    /// second statement of the stream lock), and the following statements would then run without a transaction and
    /// without the stream lock, each committing on its own. This makes such a statement fail instead.
    /// </summary>
    private const string InTransaction = "IF @@TRANCOUNT = 0 THROW 51000, 'event consumer: the transaction was ended by the server', 1;\n";

    public async Task<IngestResult> IngestAsync(string streamSource, IncomingEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var (lastApplied, fieldsJson) = await LockStreamAsync(connection, tx, e.OrderId, streamSource, ct);
        string? storedHash = null;
        Guid? atSequence = null;
        if (EventSources.IsKnown(streamSource))
        {
            storedHash = await ScalarAsync<string?>(connection, tx, "SELECT PayloadHash FROM orders.OrderEventHistory WHERE EventId = @id", ct, ("@id", e.EventId));
            atSequence = await ScalarAsync<Guid?>(connection, tx,
                "SELECT EventId FROM orders.OrderEventHistory WHERE Source = @s AND OrderId = @o AND Sequence = @q", ct,
                ("@s", streamSource), ("@o", e.OrderId), ("@q", e.Sequence));
        }

        var disposition = TimelineProjection.Classify(e, streamSource, storedHash, atSequence, lastApplied);
        var drained = 0;
        var repaired = 0;
        switch (disposition)
        {
            case Dispositions.Duplicate:
                await ExecAsync(connection, tx, "UPDATE orders.OrderEventHistory SET Duplicates = Duplicates + 1 WHERE EventId = @id", ct, ("@id", e.EventId));
                (lastApplied, repaired) = await RepairAsync(connection, tx, streamSource, e.OrderId, lastApplied, fieldsJson, ct);
                break;
            case Dispositions.Pending:
                await InsertHistoryAsync(connection, tx, streamSource, e, Dispositions.Pending, ct);
                (lastApplied, repaired) = await RepairAsync(connection, tx, streamSource, e.OrderId, lastApplied, fieldsJson, ct);
                break;
            case Dispositions.Applied:
                await InsertHistoryAsync(connection, tx, streamSource, e, Dispositions.Applied, ct);
                var fields = TimelineProjection.Apply(TimelineProjection.Deserialize(fieldsJson), e.Type, e.Data);
                lastApplied = e.Sequence;
                (lastApplied, fields, drained) = await DrainAsync(connection, tx, streamSource, e.OrderId, lastApplied, fields, ct);
                await ExecAsync(connection, tx,
                    "UPDATE orders.OrderTimelineState SET LastAppliedSequence = @q, Fields = @f, UpdatedAt = SYSUTCDATETIME() WHERE OrderId = @o AND Source = @s", ct,
                    ("@q", lastApplied), ("@f", TimelineProjection.Serialize(fields)), ("@o", e.OrderId), ("@s", streamSource));
                break;
            default:
                await RecordDeadLetterAsync(connection, tx, e.EventId, null, streamSource, e.OrderId, e.Sequence, e.Type, disposition, EventSources.TopicOf(streamSource), ct);
                break;
        }

        await CommitAsync(tx, ct);
        DispositionCounter.Add(1, new KeyValuePair<string, object?>("disposition", disposition), new KeyValuePair<string, object?>("source", streamSource));
        if (repaired > 0)
        {
            RepairCounter.Add(repaired, new KeyValuePair<string, object?>("source", streamSource));
        }

        return new IngestResult(disposition, lastApplied, drained, repaired);
    }

    /// <summary>
    /// Drains pending successors of the stream's current sequence on a delivery that applied nothing itself. Such
    /// successors should not exist (they are drained when the event before them is applied); if a stream was left
    /// behind its history, a later delivery of any of its events (a redelivery or a replay requested by the
    /// reconciler) brings it up to date under the same lock and rule.
    /// </summary>
    private static async Task<(int LastApplied, int Repaired)> RepairAsync(SqlConnection connection, SqlTransaction tx, string source, Guid orderId,
        int lastApplied, string fieldsJson, CancellationToken ct)
    {
        var (last, fields, drained) = await DrainAsync(connection, tx, source, orderId, lastApplied, TimelineProjection.Deserialize(fieldsJson), ct);
        if (drained > 0)
        {
            await ExecAsync(connection, tx,
                "UPDATE orders.OrderTimelineState SET LastAppliedSequence = @q, Fields = @f, UpdatedAt = SYSUTCDATETIME() WHERE OrderId = @o AND Source = @s", ct,
                ("@q", last), ("@f", TimelineProjection.Serialize(fields)), ("@o", orderId), ("@s", source));
        }

        return (last, drained);
    }

    /// <summary>Records a message that could not be read as an event, or one the broker dead-lettered (ids and reason only).</summary>
    public async Task RecordAsync(Guid? eventId, string? messageId, string? source, Guid? orderId, int? sequence, string? type, string reason, string? topic,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await RecordDeadLetterAsync(connection, tx, eventId, messageId, source, orderId, sequence, type, reason, topic, ct);
        await tx.CommitAsync(ct);
        DispositionCounter.Add(1, new KeyValuePair<string, object?>("disposition", reason), new KeyValuePair<string, object?>("source", source ?? "unknown"));
    }

    /// <summary>
    /// Rebuild (plan 7.8 item 5): per stream, under its lock, current state returns to sequence 0 and every history
    /// row to Pending, then history is applied from sequence 1 with the same apply-and-drain rule. Each stream's
    /// rebuilt state is compared with its live state inside the same transaction, so events still arriving for
    /// other streams do not disturb the comparison; the streams that differed are returned. The checksums over all
    /// streams before and after are informational (they differ whenever events arrive meanwhile).
    /// </summary>
    public async Task<(string Before, string After, int Streams, IReadOnlyList<string> Mismatches)> RebuildAsync(CancellationToken ct)
    {
        var before = await ChecksumAsync(null, ct);
        var streams = new List<(Guid OrderId, string Source)>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await using var list = new SqlCommand("SELECT OrderId, Source FROM orders.OrderTimelineState", connection);
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                streams.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        var mismatches = new List<string>();
        foreach (var (orderId, source) in streams)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            var (liveLast, liveFields) = await LockStreamAsync(connection, tx, orderId, source, ct);
            await ExecAsync(connection, tx,
                "UPDATE orders.OrderEventHistory SET Disposition = 'Pending', AppliedAt = NULL WHERE OrderId = @o AND Source = @s", ct, ("@o", orderId), ("@s", source));
            var (last, fields, _) = await DrainAsync(connection, tx, source, orderId, 0, new SortedDictionary<string, string>(StringComparer.Ordinal), ct);
            var liveNormalized = TimelineProjection.Serialize(TimelineProjection.Deserialize(liveFields));
            var rebuiltSerialized = TimelineProjection.Serialize(fields);
            if (last != liveLast || rebuiltSerialized != liveNormalized)
            {
                // Enough to explain the difference afterwards (the live state is replaced below): both states and the
                // stream's history by sequence, disposition after the rebuild, type, and event id; no payloads.
                var history = await HistorySummaryAsync(connection, tx, orderId, source, ct);
                mismatches.Add($"{orderId:D}/{source}: live {liveLast} {liveNormalized} rebuilt {last} {rebuiltSerialized} history {history}");
            }

            await ExecAsync(connection, tx,
                "UPDATE orders.OrderTimelineState SET LastAppliedSequence = @q, Fields = @f, UpdatedAt = SYSUTCDATETIME() WHERE OrderId = @o AND Source = @s", ct,
                ("@q", last), ("@f", TimelineProjection.Serialize(fields)), ("@o", orderId), ("@s", source));
            await CommitAsync(tx, ct);
        }

        return (before, await ChecksumAsync(null, ct), streams.Count, mismatches);
    }

    private static async Task<string> HistorySummaryAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, string source, CancellationToken ct)
    {
        await using var command = Command(
            """
            SELECT STRING_AGG(CONCAT(Sequence, ':', Disposition, ':', Type, ':', EventId, ':dup', Duplicates), ' ') WITHIN GROUP (ORDER BY Sequence)
            FROM orders.OrderEventHistory WHERE OrderId = @o AND Source = @s
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@s", source);
        return await command.ExecuteScalarAsync(ct) as string ?? "";
    }

    /// <summary>Checksum of current state, over all orders or the given ones.</summary>
    public async Task<string> ChecksumAsync(IReadOnlyCollection<Guid>? orderIds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var filter = orderIds is { Count: > 0 } ? $"WHERE OrderId IN ({string.Join(",", orderIds.Select(o => $"'{o:D}'"))})" : "";
        await using var command = new SqlCommand($"SELECT OrderId, Source, LastAppliedSequence, Fields FROM orders.OrderTimelineState {filter}", connection);
        var rows = new List<(Guid, string, int, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return TimelineProjection.Checksum(rows);
    }

    private static async Task<(int LastApplied, string Fields)> LockStreamAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, string source,
        CancellationToken ct)
    {
        if (!EventSources.IsKnown(source))
        {
            return (0, "{}");
        }

        await using var command = Command(
            """
            IF NOT EXISTS (SELECT 1 FROM orders.OrderTimelineState WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @o AND Source = @s)
                INSERT orders.OrderTimelineState (OrderId, Source) VALUES (@o, @s);
            SELECT LastAppliedSequence, Fields FROM orders.OrderTimelineState WITH (UPDLOCK, ROWLOCK) WHERE OrderId = @o AND Source = @s;
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@s", source);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetString(1));
    }

    /// <summary>Applies the pending history rows that directly follow <paramref name="lastApplied"/>, in order, until one is missing.</summary>
    private static async Task<(int LastApplied, SortedDictionary<string, string> Fields, int Drained)> DrainAsync(SqlConnection connection, SqlTransaction tx,
        string source, Guid orderId, int lastApplied, SortedDictionary<string, string> fields, CancellationToken ct)
    {
        var drained = 0;
        while (true)
        {
            Guid eventId;
            string type, data;
            await using (var next = Command(
                             "SELECT EventId, Type, Payload FROM orders.OrderEventHistory WHERE Source = @s AND OrderId = @o AND Sequence = @q AND Disposition = 'Pending'",
                             connection, tx))
            {
                next.Parameters.AddWithValue("@s", source);
                next.Parameters.AddWithValue("@o", orderId);
                next.Parameters.AddWithValue("@q", lastApplied + 1);
                await using var reader = await next.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    return (lastApplied, fields, drained);
                }

                (eventId, type, data) = (reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
            }

            fields = TimelineProjection.Apply(fields, type, data);
            lastApplied++;
            drained++;
            await ExecAsync(connection, tx, "UPDATE orders.OrderEventHistory SET Disposition = 'Applied', AppliedAt = SYSUTCDATETIME() WHERE EventId = @id", ct,
                ("@id", eventId));
        }
    }

    private static Task InsertHistoryAsync(SqlConnection connection, SqlTransaction tx, string source, IncomingEvent e, string disposition, CancellationToken ct) =>
        ExecAsync(connection, tx,
            """
            INSERT orders.OrderEventHistory (EventId, Source, OrderId, Sequence, Type, SchemaVersion, OccurredAt, PayloadHash, Payload, Disposition, AppliedAt)
            VALUES (@id, @s, @o, @q, @t, @v, @at, @h, @p, @d, CASE WHEN @d = 'Applied' THEN SYSUTCDATETIME() END);
            """, ct, ("@id", e.EventId), ("@s", source), ("@o", e.OrderId), ("@q", e.Sequence), ("@t", e.Type), ("@v", e.SchemaVersion), ("@at", e.OccurredAtUtc),
            ("@h", e.PayloadHash), ("@p", e.Data), ("@d", disposition));

    private static Task RecordDeadLetterAsync(SqlConnection connection, SqlTransaction tx, Guid? eventId, string? messageId, string? source, Guid? orderId,
        int? sequence, string? type, string reason, string? topic, CancellationToken ct) =>
        ExecAsync(connection, tx,
            """
            INSERT orders.EventDeadLetter (EventId, MessageId, Source, OrderId, Sequence, Type, Reason, Topic)
            VALUES (@id, @m, @s, @o, @q, @t, @r, @topic);
            """, ct, ("@id", (object?)eventId ?? DBNull.Value), ("@m", (object?)Truncate(messageId, 80) ?? DBNull.Value),
            ("@s", (object?)Truncate(source, 16) ?? DBNull.Value), ("@o", (object?)orderId ?? DBNull.Value), ("@q", (object?)sequence ?? DBNull.Value),
            ("@t", (object?)Truncate(type, 40) ?? DBNull.Value), ("@r", reason), ("@topic", (object?)Truncate(topic, 64) ?? DBNull.Value));

    private static string? Truncate(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];

    private static async Task CommitAsync(SqlTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.CommitAsync(ct);
        }
        catch (InvalidOperationException e) when (tx.Connection is null)
        {
            // Completed by the server (rolled back) before the commit; see InTransaction.
            throw new EventTransactionLostException(e);
        }
    }

    private static SqlCommand Command(string sql, SqlConnection connection, SqlTransaction tx) => new(InTransaction + sql, connection, tx);

    private static async Task ExecAsync(SqlConnection connection, SqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, SqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }
}
