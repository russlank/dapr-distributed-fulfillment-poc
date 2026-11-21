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
}
