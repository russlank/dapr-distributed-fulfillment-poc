using Fulfillment.Contracts.Events;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Events;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The event consumer against SQL Server: contiguous application with pending events drained when a gap is
/// filled, duplicates counted, conflicts dead-lettered without overwriting, the derived gap view, concurrent
/// deliveries of one order serialized by its state row, and a rebuild whose checksum equals live state.
/// </summary>
public sealed class EventConsumerTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private EventConsumer consumer = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("events");
        consumer = new EventConsumer(db);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IncomingEvent Event(Guid order, int sequence, string type, string data, Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), EventSources.Inventory, order, sequence, type, 1, DateTime.UtcNow, data,
            IncomingEvent.HashOf(EventSources.Inventory, order, sequence, type, 1, data));

    private static IncomingEvent[] FourPartialUpdates(Guid order) =>
    [
        Event(order, 1, EventTypes.HoldCreated, "{\"holdId\":\"a\"}"),
        Event(order, 2, EventTypes.HoldCreated, "{\"holdId\":\"b\"}"),
        Event(order, 3, EventTypes.HoldConsumed, "{\"holdId\":\"a\"}"),
        Event(order, 4, EventTypes.HoldConsumed, "{\"holdId\":\"b\"}"),
    ];

    private Task<int> LastAppliedAsync(Guid order) =>
        Db.ScalarAsync<int>(db, "SELECT LastAppliedSequence FROM orders.OrderTimelineState WHERE OrderId = @o AND Source = 'inventory'", ("@o", order));

    [Fact]
    public async Task Gap_HoldsLaterEventsPending_AndFillingItDrainsThemInOneTransaction()
    {
        var order = Guid.NewGuid();
        var e = FourPartialUpdates(order);
        Assert.Equal(Dispositions.Applied, (await consumer.IngestAsync(EventSources.Inventory, e[0], Ct)).Disposition);
        Assert.Equal(Dispositions.Pending, (await consumer.IngestAsync(EventSources.Inventory, e[2], Ct)).Disposition);
        Assert.Equal(Dispositions.Pending, (await consumer.IngestAsync(EventSources.Inventory, e[3], Ct)).Disposition);

        Assert.Equal(1, await LastAppliedAsync(order));
        Assert.Equal(2, await Db.ScalarAsync<int>(db, "SELECT MissingSequence FROM orders.OrderEventGaps WHERE OrderId = @o", ("@o", order)));
        var fields = await Db.ScalarAsync<string>(db, "SELECT Fields FROM orders.OrderTimelineState WHERE OrderId = @o", ("@o", order));
        Assert.Equal("{\"hold:a\":\"Held\"}", fields);

        var filled = await consumer.IngestAsync(EventSources.Inventory, e[1], Ct);
        Assert.Equal((Dispositions.Applied, 4, 2), (filled.Disposition, filled.LastAppliedSequence, filled.Drained));
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.OrderEventGaps WHERE OrderId = @o", ("@o", order)));
        Assert.Equal(4, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = @o AND Disposition = 'Applied'", ("@o", order)));
    }

    [Fact]
    public async Task DuplicatesAreCounted_ConflictsAreDeadLetteredAndNeverOverwrite()
    {
        var order = Guid.NewGuid();
        var e = FourPartialUpdates(order);
        await consumer.IngestAsync(EventSources.Inventory, e[0], Ct);
        var checksum = await consumer.ChecksumAsync([order], Ct);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Dispositions.Duplicate, (await consumer.IngestAsync(EventSources.Inventory, e[0], Ct)).Disposition);
        }

        Assert.Equal(Dispositions.EventIdPayloadConflict,
            (await consumer.IngestAsync(EventSources.Inventory, Event(order, 1, EventTypes.HoldReleased, "{\"holdId\":\"a\"}", e[0].EventId), Ct)).Disposition);
        Assert.Equal(Dispositions.SequenceConflict,
            (await consumer.IngestAsync(EventSources.Inventory, Event(order, 1, EventTypes.HoldCreated, "{\"holdId\":\"z\"}"), Ct)).Disposition);
        Assert.Equal(Dispositions.SourceMismatch,
            (await consumer.IngestAsync(EventSources.Inventory, Event(order, 2, EventTypes.PaymentOperationRecorded, "{}"), Ct)).Disposition);

        Assert.Equal(3, await Db.ScalarAsync<int>(db, "SELECT Duplicates FROM orders.OrderEventHistory WHERE EventId = @e", ("@e", e[0].EventId)));
        Assert.Equal("EventIdPayloadConflict,SequenceConflict,SourceMismatch", await Db.ScalarAsync<string>(db,
            "SELECT STRING_AGG(Reason, ',') WITHIN GROUP (ORDER BY Reason) FROM orders.EventDeadLetter WHERE OrderId = @o", ("@o", order)));
        Assert.Equal(checksum, await consumer.ChecksumAsync([order], Ct));
    }

    [Fact]
    public async Task ConcurrentDeliveriesOfOneOrder_AreSerializedByItsStateRow()
    {
        for (var round = 0; round < 10; round++)
        {
            var order = Guid.NewGuid();
            var e = FourPartialUpdates(order);
            // Every event delivered twice, all at once, on separate connections.
            await Task.WhenAll(e.Concat(e).Select(x => Task.Run(() => consumer.IngestAsync(EventSources.Inventory, x, Ct), Ct)));
            Assert.Equal(4, await LastAppliedAsync(order));
            Assert.Equal(4, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = @o AND Disposition = 'Applied'", ("@o", order)));
            Assert.Equal(4, await Db.ScalarAsync<int>(db, "SELECT SUM(Duplicates) FROM orders.OrderEventHistory WHERE OrderId = @o", ("@o", order)));
        }
    }

    [Fact]
    public async Task Rebuild_FromHistory_GivesTheLiveChecksum()
    {
        foreach (var order in Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()))
        {
            var e = FourPartialUpdates(order);
            foreach (var index in new[] { 3, 1, 0, 2 })
            {
                await consumer.IngestAsync(EventSources.Inventory, e[index], Ct);
            }
        }

        var (before, after, streams, mismatches) = await consumer.RebuildAsync(Ct);
        Assert.Empty(mismatches);
        Assert.Equal(before, after);
        Assert.True(streams >= 5);
    }

    [Fact]
    public async Task AStreamLeftBehindItsHistory_IsRepairedByTheNextDuplicateOrEarlyDelivery()
    {
        // The state seen in a full run: sequences 3 and 4 in history as pending while the stream stood at 2, and every
        // later delivery of 3 or 4 (replays requested by the reconciler) only counted as a duplicate.
        foreach (var early in new[] { false, true })
        {
            var order = Guid.NewGuid();
            var e = FourPartialUpdates(order);
            await consumer.IngestAsync(EventSources.Inventory, e[0], Ct);
            await consumer.IngestAsync(EventSources.Inventory, e[1], Ct);
            foreach (var x in new[] { e[2], e[3] })
            {
                await Db.ExecuteAsync(db,
                    """
                    INSERT orders.OrderEventHistory (EventId, Source, OrderId, Sequence, Type, SchemaVersion, OccurredAt, PayloadHash, Payload, Disposition)
                    VALUES (@id, 'inventory', @o, @q, @t, 1, SYSUTCDATETIME(), @h, @p, 'Pending')
                    """, ("@id", x.EventId), ("@o", order), ("@q", x.Sequence), ("@t", x.Type), ("@h", x.PayloadHash), ("@p", x.Data));
            }

            Assert.Equal(2, await LastAppliedAsync(order));
            var delivery = early ? Event(order, 6, EventTypes.HoldCreated, "{\"holdId\":\"c\"}") : e[2];
            var result = await consumer.IngestAsync(EventSources.Inventory, delivery, Ct);

            Assert.Equal((early ? Dispositions.Pending : Dispositions.Duplicate, 4, 2), (result.Disposition, result.LastAppliedSequence, result.Repaired));
            Assert.Equal(4, await LastAppliedAsync(order));
            Assert.Equal("{\"hold:a\":\"Consumed\",\"hold:b\":\"Consumed\"}",
                await Db.ScalarAsync<string>(db, "SELECT Fields FROM orders.OrderTimelineState WHERE OrderId = @o AND Source = 'inventory'", ("@o", order)));
            Assert.Equal(0, await Db.ScalarAsync<int>(db,
                "SELECT COUNT(*) FROM orders.OrderEventHistory WHERE OrderId = @o AND Sequence <= 4 AND Disposition = 'Pending'", ("@o", order)));
        }
    }

    [Fact]
    public async Task Rebuild_ReportsADivergedStreamWithBothStatesAndItsHistory_AndRestoresIt()
    {
        var order = Guid.NewGuid();
        var e = FourPartialUpdates(order);
        foreach (var x in e)
        {
            await consumer.IngestAsync(EventSources.Inventory, x, Ct);
        }

        await Db.ExecuteAsync(db, "UPDATE orders.OrderTimelineState SET LastAppliedSequence = 3, Fields = '{}' WHERE OrderId = @o", ("@o", order));
        var (_, _, _, mismatches) = await consumer.RebuildAsync(Ct);

        var report = Assert.Single(mismatches, m => m.StartsWith($"{order:D}/inventory", StringComparison.Ordinal));
        Assert.Contains("live 3 {}", report, StringComparison.Ordinal);
        Assert.Contains("rebuilt 4 {\"hold:a\":\"Consumed\",\"hold:b\":\"Consumed\"}", report, StringComparison.Ordinal);
        Assert.Contains($"4:Applied:HoldConsumed:{e[3].EventId}".ToUpperInvariant(), report.ToUpperInvariant(), StringComparison.Ordinal);
        Assert.Equal(4, await LastAppliedAsync(order));
    }
}
