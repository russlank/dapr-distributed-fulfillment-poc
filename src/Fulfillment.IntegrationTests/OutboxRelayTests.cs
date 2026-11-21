using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Fulfillment.Contracts.Events;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.Messaging.Outbox;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The outbox relay against SQL Server (the inventory outbox): appends in the business transaction, leases that
/// keep a second relay away until they expire, takeover after expiry, idempotent and replay-safe marking,
/// failures with backoff and the Stalled flag, and competing relays that publish everything at least once.
/// </summary>
public sealed class OutboxRelayTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Table = "inventory.Outbox";
    private string db = "";
    private OutboxStore store = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateInventoryDbAsync("outbox");
        store = new OutboxStore(db, new OutboxTable(EventSources.Inventory, Table));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> AppendAsync(int count, Guid? order = null)
    {
        var orderId = order ?? Guid.NewGuid();
        await using var connection = new SqlConnection(db);
        await connection.OpenAsync(Ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        for (var i = 0; i < count; i++)
        {
            // HoldId is required by this table; the outbox columns come from defaults and the shared append.
            await using var command = new SqlCommand(
                $"""
                INSERT {Table} (EventId, OrderId, Sequence, EventType, HoldId, Payload)
                SELECT NEWID(), @o, ISNULL(MAX(Sequence), 0) + 1, 'HoldCreated', NEWID(), @p FROM {Table} WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @o;
                """, connection, tx);
            command.Parameters.AddWithValue("@o", orderId);
            command.Parameters.AddWithValue("@p", "{\"holdId\":\"x\"}");
            await command.ExecuteNonQueryAsync(Ct);
        }

        await tx.CommitAsync(Ct);
        return orderId;
    }

    private static DaprEventPublisher Publisher(Func<HttpRequestMessage, HttpStatusCode> answer, ConcurrentBag<string>? published = null) =>
        new(new HttpClient(new Handler(answer, published)) { BaseAddress = new Uri("http://127.0.0.1:3500/") }, new OutboxRelayOptions());

    private OutboxRelay Relay(DaprEventPublisher publisher, string owner, Func<string, string, CancellationToken, Task>? checkpoint = null) =>
        new(store, publisher, new OutboxRelayOptions { Lease = TimeSpan.FromSeconds(30), StallThreshold = 3, FirstBackoff = TimeSpan.FromMilliseconds(1) }, owner,
            checkpoint ?? ((_, _, _) => Task.CompletedTask), NullLogger<OutboxRelay>.Instance);

    [Fact]
    public async Task Append_RolledBack_LeavesNoRowAndKeepsTheSequenceGapless()
    {
        // The shared append, on an outbox of the shared shape (shipping).
        var shipping = await sql.CreateShippingDbAsync("outbox-append");
        var order = Guid.NewGuid();
        async Task<int> AppendShippingAsync(bool commit)
        {
            await using var connection = new SqlConnection(shipping);
            await connection.OpenAsync(Ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
            var sequence = await OutboxStore.AppendAsync(connection, tx, "shipping.Outbox", order, EventTypes.ShipmentDispatched, "{}", Ct);
            if (commit)
            {
                await tx.CommitAsync(Ct);
            }
            else
            {
                await tx.RollbackAsync(Ct);
            }

            return sequence;
        }

        Assert.Equal(1, await AppendShippingAsync(commit: true));
        Assert.Equal(2, await AppendShippingAsync(commit: false));
        Assert.Equal(2, await AppendShippingAsync(commit: true));
        var rows = await Db.RowsAsync(shipping, "SELECT Sequence, State, Attempts, Generation FROM shipping.Outbox WHERE OrderId = @o ORDER BY Sequence", ("@o", order));
        Assert.Equal([1, 2], rows.Select(r => (int)r["Sequence"]!));
        Assert.All(rows, r => Assert.Equal(("Pending", 0, 0), ((string)r["State"]!, (int)r["Attempts"]!, (int)r["Generation"]!)));
    }

    [Fact]
    public async Task Claim_LiveLeaseKeepsOthersAway_ExpiredLeaseIsTakenOver()
    {
        var order = await AppendAsync(2);
        var first = await store.ClaimAsync("relay-a", 10, TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(2, first.Count(r => r.OrderId == order));
        Assert.DoesNotContain(await store.ClaimAsync("relay-b", 10, TimeSpan.FromSeconds(30), Ct), r => r.OrderId == order);

        // The lease ran out (moved back past any clock step rather than waited for).
        await Db.ExecuteAsync(db, $"UPDATE {Table} SET LeaseUntil = DATEADD(second, -{SqlClock.MarginSeconds}, SYSUTCDATETIME()) WHERE OrderId = @o", ("@o", order));
        var taken = await store.ClaimAsync("relay-b", 10, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(2, taken.Count(r => r.OrderId == order));
        Assert.Equal("relay-b", await Db.ScalarAsync<string>(db, $"SELECT MAX(LeaseOwner) FROM {Table} WHERE OrderId = @o", ("@o", order)));
    }

    [Fact]
    public async Task Mark_IsIdempotent_AndAReplayRequestedMeanwhileIsNotSwallowed()
    {
        var order = await AppendAsync(2);
        var rows = (await store.ClaimAsync("relay-a", 10, TimeSpan.FromSeconds(30), Ct)).Where(r => r.OrderId == order).ToList();
        Assert.True(await store.MarkPublishedAsync(rows[0].Id, rows[0].Generation, Ct));
        Assert.False(await store.MarkPublishedAsync(rows[0].Id, rows[0].Generation, Ct));

        // A replay between the publication and the mark: the mark of the old generation does not apply.
        var replay = await store.ReplayAsync(order, 2, Ct);
        Assert.Equal(1, replay.Replayed);
        Assert.False(await store.MarkPublishedAsync(rows[1].Id, rows[1].Generation, Ct));
        Assert.Equal("Pending", await Db.ScalarAsync<string>(db, $"SELECT State FROM {Table} WHERE Id = @id", ("@id", rows[1].Id)));

        // Replaying from 1 returns the published row to Pending with a new generation; nothing is deleted.
        var all = await store.ReplayAsync(order, 1, Ct);
        Assert.Equal((2, 2), (all.Replayed, all.MaxSequence));
        Assert.Equal(2, await Db.ScalarAsync<int>(db, $"SELECT COUNT(*) FROM {Table} WHERE OrderId = @o AND State = 'Pending'", ("@o", order)));
    }

    [Fact]
    public async Task Failure_OnlyTheLeaseHolderRecordsIt_BackoffThenStalledAndStillRetried()
    {
        var order = await AppendAsync(1);
        var row = (await store.ClaimAsync("relay-a", 10, TimeSpan.FromSeconds(30), Ct)).Single(r => r.OrderId == order);
        await store.RecordFailureAsync(row.Id, row.Generation, "relay-b", "not mine", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 3, Ct);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, $"SELECT Attempts FROM {Table} WHERE Id = @id", ("@id", row.Id)));

        await store.RecordFailureAsync(row.Id, row.Generation, "relay-a", "broker down", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 3, Ct);
        var after = (await Db.RowsAsync(db, $"SELECT Attempts, State, Stalled, LeaseOwner, DATEDIFF(millisecond, SYSUTCDATETIME(), NextAttemptAt) AS DueIn FROM {Table} WHERE Id = @id",
            ("@id", row.Id))).Single();
        Assert.Equal((1, "Pending", false), ((int)after["Attempts"]!, (string)after["State"]!, (bool)after["Stalled"]!));
        Assert.Null(after["LeaseOwner"]);
        // About the first backoff (1 s) ahead; the bounds allow for the server clock's steps.
        Assert.InRange((int)after["DueIn"]!, -3000, 5000);

        var relay = Relay(Publisher(_ => HttpStatusCode.ServiceUnavailable), "relay-c");
        for (var i = 0; i < 4; i++)
        {
            await Db.ExecuteAsync(db, $"UPDATE {Table} SET NextAttemptAt = DATEADD(second, -{SqlClock.MarginSeconds}, SYSUTCDATETIME()) WHERE Id = @id", ("@id", row.Id));
            await relay.RunOnceAsync(Ct);
        }

        var stalled = (await Db.RowsAsync(db, $"SELECT Attempts, State, Stalled FROM {Table} WHERE Id = @id", ("@id", row.Id))).Single();
        Assert.Equal((5, "Pending", true), ((int)stalled["Attempts"]!, (string)stalled["State"]!, (bool)stalled["Stalled"]!));

        await Db.ExecuteAsync(db, $"UPDATE {Table} SET NextAttemptAt = DATEADD(second, -{SqlClock.MarginSeconds}, SYSUTCDATETIME()) WHERE Id = @id", ("@id", row.Id));
        await Relay(Publisher(_ => HttpStatusCode.NoContent), "relay-c").RunOnceAsync(Ct);
        var published = (await Db.RowsAsync(db, $"SELECT State, Stalled FROM {Table} WHERE Id = @id", ("@id", row.Id))).Single();
        Assert.Equal(("Published", false), ((string)published["State"]!, (bool)published["Stalled"]!));
    }

    [Fact]
    public async Task CompetingRelays_PublishEveryRowAtLeastOnce_AndEndPublished()
    {
        var orders = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            orders.Add(await AppendAsync(5));
        }

        var published = new ConcurrentBag<string>();
        var publisher = Publisher(_ => HttpStatusCode.NoContent, published);
        var relays = Enumerable.Range(0, 3).Select(i => Relay(publisher, $"relay-{i}")).ToList();
        for (var round = 0; round < 10; round++)
        {
            await Task.WhenAll(relays.Select(r => r.RunOnceAsync(Ct)));
        }

        var ids = await Db.RowsAsync(db, $"SELECT EventId, State FROM {Table} WHERE OrderId IN ({string.Join(",", orders.Select(o => $"'{o}'"))})");
        Assert.Equal(50, ids.Count);
        Assert.All(ids, r => Assert.Equal("Published", r["State"]));
        var distinct = published.Distinct().ToHashSet();
        Assert.All(ids, r => Assert.Contains(((Guid)r["EventId"]!).ToString(), distinct));
    }

    [Fact]
    public async Task CrashAfterAcknowledgement_LeavesTheRowPending_AndANextRelayPublishesItAgain()
    {
        var order = await AppendAsync(1);
        var published = new ConcurrentBag<string>();
        var publisher = Publisher(_ => HttpStatusCode.NoContent, published);
        var crashing = Relay(publisher, "relay-a", (name, _, _) =>
            name == OutboxRelay.AfterBrokerAckBeforeOutboxMarkSent ? throw new InvalidOperationException("crash") : Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.RunOnceAsync(Ct));
        Assert.Equal("Pending", await Db.ScalarAsync<string>(db, $"SELECT State FROM {Table} WHERE OrderId = @o", ("@o", order)));

        // The lease of the crashed relay expires; another relay publishes the same event again.
        await Db.ExecuteAsync(db, $"UPDATE {Table} SET LeaseUntil = DATEADD(second, -{SqlClock.MarginSeconds}, SYSUTCDATETIME()) WHERE OrderId = @o", ("@o", order));
        await Relay(publisher, "relay-b").RunOnceAsync(Ct);
        var eventId = (await Db.ScalarAsync<Guid>(db, $"SELECT EventId FROM {Table} WHERE OrderId = @o", ("@o", order))).ToString();
        Assert.Equal(2, published.Count(p => p == eventId));
        Assert.Equal("Published", await Db.ScalarAsync<string>(db, $"SELECT State FROM {Table} WHERE OrderId = @o", ("@o", order)));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpStatusCode> answer, ConcurrentBag<string>? published) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = answer(request);
            if (published is not null && (int)status < 300)
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                published.Add(doc.RootElement.GetProperty("eventId").GetString()!);
            }

            return new HttpResponseMessage(status);
        }
    }
}
