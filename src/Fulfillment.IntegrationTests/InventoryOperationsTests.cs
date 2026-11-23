extern alias inventory;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.ServiceDefaults;
using inventory::Fulfillment.InventoryService.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The guarded inventory operations against SQL Server, without actors: every guarantee here holds
/// in the database itself (concurrent callers meet only in SQL). Assertions read the rows.
/// </summary>
public sealed class InventoryOperationsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private string db = "";
    private InventoryOperations ops = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateInventoryDbAsync("inventory");
        ops = new InventoryOperations(db, "test-replica");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Reserve_ManyConcurrentOnOneStockRow_NeverOversells()
    {
        await Stock.SetAsync(db, "W2", "SKU-T1", 20);

        var results = await Task.WhenAll(Enumerable.Range(1, 60).Select(i => Task.Run(() =>
            ops.ReserveAsync(Reserve(Guid.NewGuid(), 1, "SKU-T1", 1, "W2"), TestContext.Current.CancellationToken))));

        Assert.Equal(20, results.Count(r => r.Outcome == InventoryOutcomes.Reserved));
        Assert.Equal(40, results.Count(r => r.Outcome == InventoryOutcomes.InsufficientStock));
        Assert.Equal((20, 20), await Stock.GetAsync(db, "W2", "SKU-T1"));
        Assert.Equal(20, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = 'SKU-T1' AND State = 'Held'"));
    }

    [Fact]
    public async Task Reserve_SameKeyRacingTwenty_OneEffectAndEveryCallerReadsIt()
    {
        await Stock.SetAsync(db, "W1", "SKU-T2", 5);
        var command = Reserve(Guid.NewGuid(), 1, "SKU-T2", 2, "W1");

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(_ => Task.Run(() => ops.ReserveAsync(command, TestContext.Current.CancellationToken))));

        Assert.Single(results.Select(r => r.HoldId).Distinct());
        Assert.Equal(19, results.Count(r => r.Replayed));
        Assert.Equal((5, 2), await Stock.GetAsync(db, "W1", "SKU-T2"));
        Assert.Equal((1, 19), (await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.ProcessedOperation WHERE OperationKey = @k", ("@k", command.OperationKey)),
            await Db.ScalarAsync<int>(db, "SELECT Replays FROM inventory.ProcessedOperation WHERE OperationKey = @k", ("@k", command.OperationKey))));
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId = @o", ("@o", command.OrderId)));
    }

    [Fact]
    public async Task Reserve_InsufficientStock_IsStoredAndReplayedEvenAfterStockArrives()
    {
        await Stock.SetAsync(db, "W1", "SKU-T3", 1);
        var command = Reserve(Guid.NewGuid(), 1, "SKU-T3", 2, "W1");

        var first = await ops.ReserveAsync(command, TestContext.Current.CancellationToken);
        await Stock.SetAsync(db, "W1", "SKU-T3", 10);
        var again = await ops.ReserveAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal((InventoryOutcomes.InsufficientStock, InventoryOutcomes.InsufficientStock, true), (first.Outcome, again.Outcome, again.Replayed));
        Assert.Equal((10, 0), await Stock.GetAsync(db, "W1", "SKU-T3"));
    }

    [Fact]
    public async Task Release_RacingTheSweeperOnAnExpiredHold_OneTransitionAndReservedBackOnce()
    {
        for (var round = 0; round < 10; round++)
        {
            await Stock.SetAsync(db, "W1", "SKU-T4", 3);
            var order = Guid.NewGuid();
            var hold = await ops.ReserveAsync(Reserve(order, 1, "SKU-T4", 2, "W1", TimeSpan.FromMilliseconds(200)), TestContext.Current.CancellationToken);
            await WaitUntilDueAsync(hold.HoldId!.Value);

            var release = Task.Run(() => ops.ReleaseAsync(new ReleaseCommand(order, hold.HoldId.Value, InventoryKeys.Release(order, hold.HoldId.Value)), TestContext.Current.CancellationToken));
            var sweeps = Enumerable.Range(0, 3).Select(_ => Task.Run(() => ops.ExpireNextDueAsync(TestContext.Current.CancellationToken))).ToList();
            var released = await release;
            var expired = (await Task.WhenAll(sweeps)).Count(h => h == hold.HoldId);

            // Exactly one of them took the hold out of Held.
            Assert.Equal(1, (released.Outcome == InventoryOutcomes.Released ? 1 : 0) + expired);
            Assert.Equal((3, 0), await Stock.GetAsync(db, "W1", "SKU-T4"));
            Assert.Equal(["HoldCreated", released.Outcome == InventoryOutcomes.Released ? "HoldReleased" : "HoldExpired"], await OutboxTypesAsync(order));
        }
    }

    [Fact]
    public async Task Consume_RacingTheSweeper_OneTransition()
    {
        for (var round = 0; round < 10; round++)
        {
            await Stock.SetAsync(db, "W1", "SKU-T5", 4);
            var order = Guid.NewGuid();
            var hold = await ops.ReserveAsync(Reserve(order, 1, "SKU-T5", 1, "W1", TimeSpan.FromMilliseconds(150)), TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            var consume = Task.Run(() => ops.ConsumeOrderAsync(order, 1, InventoryKeys.Consume(order), TestContext.Current.CancellationToken));
            var sweep = Task.Run(async () =>
            {
                await Task.Delay(60);
                return await ops.ExpireNextDueAsync(TestContext.Current.CancellationToken);
            });
            var consumed = await consume;
            var expired = await sweep == hold.HoldId;

            var (onHand, reserved) = await Stock.GetAsync(db, "W1", "SKU-T5");
            Assert.Equal(0, reserved);
            Assert.False(consumed.Outcome == InventoryOutcomes.Consumed && expired);
            Assert.Equal(consumed.Outcome == InventoryOutcomes.Consumed ? 3 : 4, onHand);
            Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = @o AND State <> 'Held'", ("@o", order))
                + await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = @o AND State = 'Held'", ("@o", order)) * 0);
        }
    }

    [Fact]
    public async Task Consume_OneHoldExpired_ConsumesNothingAndLeavesTheOrderOpenForRelease()
    {
        await Stock.SetAsync(db, "W1", "SKU-T6", 5);
        await Stock.SetAsync(db, "W1", "SKU-T7", 5);
        var order = Guid.NewGuid();
        await ops.ReserveAsync(Reserve(order, 1, "SKU-T6", 2, "W1"), TestContext.Current.CancellationToken);
        var shortLived = await ops.ReserveAsync(Reserve(order, 2, "SKU-T7", 1, "W1", TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken);
        await WaitUntilDueAsync(shortLived.HoldId!.Value);

        var consumed = await ops.ConsumeOrderAsync(order, 2, InventoryKeys.Consume(order), TestContext.Current.CancellationToken);
        var replay = await ops.ConsumeOrderAsync(order, 2, InventoryKeys.Consume(order), TestContext.Current.CancellationToken);

        Assert.Equal((InventoryOutcomes.HoldExpired, 0), (consumed.Outcome, consumed.Consumed));
        Assert.Equal((InventoryOutcomes.HoldExpired, true), (replay.Outcome, replay.Replayed));
        Assert.Equal((5, 2), await Stock.GetAsync(db, "W1", "SKU-T6"));
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.OrderClosure WHERE OrderId = @o", ("@o", order)));
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = @o AND State = 'Consumed'", ("@o", order)));
    }

    [Fact]
    public async Task Consume_AllHeld_DecrementsOnHandAndReservedOnceAndClosesTheOrder()
    {
        await Stock.SetAsync(db, "W1", "SKU-T8", 10);
        await Stock.SetAsync(db, "W2", "SKU-T9", 10);
        var order = Guid.NewGuid();
        await ops.ReserveAsync(Reserve(order, 1, "SKU-T8", 3, "W1"), TestContext.Current.CancellationToken);
        await ops.ReserveAsync(Reserve(order, 2, "SKU-T9", 4, "W2"), TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => ops.ConsumeOrderAsync(order, 2, InventoryKeys.Consume(order), TestContext.Current.CancellationToken))));
        var late = await ops.ReserveAsync(Reserve(order, 3, "SKU-T8", 1, "W1"), TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(InventoryOutcomes.Consumed, r.Outcome));
        Assert.Equal(4, results.Count(r => r.Replayed));
        Assert.Equal((7, 0), await Stock.GetAsync(db, "W1", "SKU-T8"));
        Assert.Equal((6, 0), await Stock.GetAsync(db, "W2", "SKU-T9"));
        Assert.Equal(InventoryOutcomes.OrderClosed, late.Outcome);
        Assert.Equal(["HoldCreated", "HoldCreated", "HoldConsumed", "HoldConsumed"], await OutboxTypesAsync(order));
    }

    [Fact]
    public async Task Close_RacingALateReserve_LeavesNoHeldHoldBehind()
    {
        await Stock.SetAsync(db, "W1", "SKU-T10", 1000);
        for (var round = 0; round < 25; round++)
        {
            var order = Guid.NewGuid();
            await ops.ReserveAsync(Reserve(order, 1, "SKU-T10", 1, "W1"), TestContext.Current.CancellationToken);
            var late = Task.Run(() => ops.ReserveAsync(Reserve(order, 2, "SKU-T10", 1, "W1"), TestContext.Current.CancellationToken));
            var close = Task.Run(async () =>
            {
                var held = await ops.CloseOrderAsync(order, "Released", InventoryKeys.Close(order), TestContext.Current.CancellationToken);
                foreach (var h in held)
                {
                    await ops.ReleaseAsync(new ReleaseCommand(order, h.HoldId, InventoryKeys.Release(order, h.HoldId)), TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken);
            await Task.WhenAll(late, close);

            // A reserve that won the race was listed by the close and released; one that lost was refused.
            Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE OrderId = @o AND State = 'Held'", ("@o", order)));
            Assert.Contains((await late).Outcome, new[] { InventoryOutcomes.Reserved, InventoryOutcomes.OrderClosed });
        }

        Assert.Equal((1000, 0), await Stock.GetAsync(db, "W1", "SKU-T10"));
    }

    [Fact]
    public async Task Outbox_ConcurrentTransitionsOfOneOrder_GetAGaplessSequence()
    {
        await Stock.SetAsync(db, "W1", "SKU-T11", 100);
        var order = Guid.NewGuid();
        var holds = await Task.WhenAll(Enumerable.Range(1, 10).Select(l => Task.Run(() => ops.ReserveAsync(Reserve(order, l, "SKU-T11", 1, "W1"), TestContext.Current.CancellationToken))));
        await Task.WhenAll(holds.Select(h => Task.Run(() => ops.ReleaseAsync(new ReleaseCommand(order, h.HoldId!.Value, InventoryKeys.Release(order, h.HoldId.Value)), TestContext.Current.CancellationToken))));

        var sequences = (await Db.RowsAsync(db, "SELECT Sequence FROM inventory.Outbox WHERE OrderId = @o ORDER BY Sequence", ("@o", order))).Select(r => (int)r["Sequence"]!).ToList();
        Assert.Equal(Enumerable.Range(1, 20), sequences);
    }

    [Fact]
    public async Task Sweeper_TwoReplicas_OnlyTheLeaseHolderSweepsAndTheOtherTakesOverAfterTheLease()
    {
        await Stock.SetAsync(db, "W1", "SKU-T12", 10);
        var options = Options.Create(new InventoryOptions { SweeperLease = TimeSpan.FromSeconds(2), SweepBatch = 10 });
        var a = Sweeper(options, "replica-a");
        var b = Sweeper(options, "replica-b");
        for (var i = 0; i < 4; i++)
        {
            await ops.ReserveAsync(Reserve(Guid.NewGuid(), 1, "SKU-T12", 1, "W1", TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken);
        }

        await WaitUntilAllDueAsync("SKU-T12");
        var byA = await a.SweepOnceAsync(TestContext.Current.CancellationToken);
        var byB = await b.SweepOnceAsync(TestContext.Current.CancellationToken);
        await ops.ReserveAsync(Reserve(Guid.NewGuid(), 1, "SKU-T12", 1, "W1", TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken);
        await WaitForLeaseExpiryAsync();
        await WaitUntilAllDueAsync("SKU-T12");
        var byBLater = await b.SweepOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((4, 0, 1), (byA, byB, byBLater));
        Assert.Equal((10, 0), await Stock.GetAsync(db, "W1", "SKU-T12"));
        Assert.Equal(5, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = 'SKU-T12' AND State = 'Expired'"));
    }

    [Fact]
    public async Task ReserveLine_AllocatesTheWholeLineFromTheFirstWarehouseThatCanAndReplaysTheSameWay()
    {
        await Stock.SetAsync(db, "W1", "SKU-T13", 2);
        await Stock.SetAsync(db, "W2", "SKU-T13", 5);
        var activity = new ReserveOrderLineActivity(ops, new SqlCoordinator(ops), new Failpoints(null, NullLogger<Failpoints>.Instance),
            Options.Create(new InventoryOptions { HoldTtl = Ttl }));
        var request = new ReserveLineRequest(Guid.NewGuid(), 1, "SKU-T13", 3);

        var first = await activity.RunAsync(null!, request);
        var again = await activity.RunAsync(null!, request);
        var tooMuch = await activity.RunAsync(null!, request with { OrderId = Guid.NewGuid(), Quantity = 6 });

        Assert.Equal((InventoryOutcomes.Reserved, "W2"), (first.Outcome, first.WarehouseId));
        Assert.Equal(["W1:InsufficientStock", "W2:Reserved"], first.Tried);
        Assert.Equal(first.HoldId, again.HoldId);
        Assert.Equal(["W1:InsufficientStock(replay)", "W2:Reserved(replay)"], again.Tried);
        Assert.Equal(InventoryOutcomes.InsufficientStock, tooMuch.Outcome);
        Assert.Equal((2, 0), await Stock.GetAsync(db, "W1", "SKU-T13"));
        Assert.Equal((5, 3), await Stock.GetAsync(db, "W2", "SKU-T13"));
    }

    [Theory]
    [InlineData("UPDATE inventory.StockItem SET Reserved = OnHand + 1 WHERE Sku = 'SKU-1'", "CK_StockItem_Reserved")]
    [InlineData("UPDATE inventory.StockItem SET Reserved = -1 WHERE Sku = 'SKU-1'", "CK_StockItem_Reserved")]
    [InlineData("UPDATE inventory.StockItem SET OnHand = -1, Reserved = 0 WHERE Sku = 'SKU-1'", "CK_StockItem_")]
    [InlineData("INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy) VALUES (NEWID(), 1, 'W1', 'SKU-1', 1, 'Released', DATEADD(minute, 1, SYSUTCDATETIME()), 'x')", "CK_Hold_Lifecycle")]
    [InlineData("INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy) VALUES (NEWID(), 1, 'W1', 'SKU-1', 1, 'Lost', DATEADD(minute, 1, SYSUTCDATETIME()), 'x')", "CK_Hold_")]
    [InlineData("INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy) VALUES (NEWID(), 1, 'W1', 'SKU-1', 0, 'Held', DATEADD(minute, 1, SYSUTCDATETIME()), 'x')", "CK_Hold_Quantity")]
    [InlineData("INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy) VALUES (NEWID(), 1, 'W9', 'SKU-1', 1, 'Held', DATEADD(minute, 1, SYSUTCDATETIME()), 'x')", "FK_Hold_StockItem")]
    [InlineData("INSERT inventory.Outbox (OrderId, Sequence, EventType, HoldId, Payload) VALUES (NEWID(), 0, 'HoldCreated', NEWID(), '{}')", "CK_Outbox_Sequence")]
    [InlineData("INSERT inventory.ProcessedOperation (OperationKey, Operation, OrderId, Outcome, ProcessedBy, Replays) VALUES ('k', 'Reserve', NEWID(), 'Maybe', 'x', 0)", "CK_ProcessedOperation_Outcome")]
    public async Task Schema_RejectsViolationsOfTheInventoryInvariants(string statement, string constraint)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => Db.ExecuteAsync(db, statement));

        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_SecondHoldForTheSameLine_IsRejected()
    {
        var order = Guid.NewGuid();
        await ops.ReserveAsync(Reserve(order, 1, "SKU-1", 1, "W1"), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<SqlException>(() => Db.ExecuteAsync(db,
            $"INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy) VALUES ('{order}', 1, 'W1', 'SKU-2', 1, 'Held', DATEADD(minute, 1, SYSUTCDATETIME()), 'x')"));

        Assert.Contains("UX_Hold_Order_Line", error.Message, StringComparison.Ordinal);
    }

    private static ReserveCommand Reserve(Guid order, int line, string sku, int quantity, string warehouse, TimeSpan? ttl = null) =>
        new(order, line, sku, quantity, warehouse, InventoryKeys.Reserve(order, line, warehouse), ttl ?? Ttl);

    private HoldSweeper Sweeper(IOptions<InventoryOptions> options, string replica) =>
        new(ops, options, new RuntimeIdentity("inventory-service", replica, "test", null, null, null, null, "release", "absent", "test"), db, NullLogger<HoldSweeper>.Instance);

    private async Task<List<string>> OutboxTypesAsync(Guid order) =>
        [.. (await Db.RowsAsync(db, "SELECT EventType FROM inventory.Outbox WHERE OrderId = @o ORDER BY Sequence", ("@o", order))).Select(r => (string)r["EventType"]!)];

    /// <summary>Waits until SQL time passed the hold's expiry (the server clock can step; see SqlClock).</summary>
    private async Task WaitUntilDueAsync(Guid holdId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await Db.ScalarAsync<int>(db, "SELECT CASE WHEN ExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END FROM inventory.Hold WHERE HoldId = @h", ("@h", holdId)) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the hold did not become due by SQL time");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until SQL time passed the expiry of every Held hold of the SKU (a fixed delay is not enough: the server clock can step).</summary>
    private async Task WaitUntilAllDueAsync(string sku)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Hold WHERE Sku = @s AND State = 'Held' AND ExpiresAt > SYSUTCDATETIME()", ("@s", sku)) > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the holds did not become due by SQL time");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private async Task WaitForLeaseExpiryAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await Db.ScalarAsync<int>(db, "SELECT CASE WHEN ExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END FROM inventory.SweeperLease WHERE Name = 'hold-expiry'") == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the lease did not expire by SQL time");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Test-only stock setup (the inventory has no restock path).</summary>
public static class Stock
{
    public static Task SetAsync(string db, string warehouse, string sku, int onHand) =>
        Db.ExecuteAsync(db, $"""
            MERGE inventory.StockItem AS t USING (SELECT '{warehouse}' AS WarehouseId, '{sku}' AS Sku) AS s
            ON t.WarehouseId = s.WarehouseId AND t.Sku = s.Sku
            WHEN MATCHED THEN UPDATE SET OnHand = {onHand}, Reserved = 0
            WHEN NOT MATCHED THEN INSERT (WarehouseId, Sku, OnHand, Reserved) VALUES ('{warehouse}', '{sku}', {onHand}, 0);
            """);

    public static async Task<(int OnHand, int Reserved)> GetAsync(string db, string warehouse, string sku)
    {
        var row = (await Db.RowsAsync(db, "SELECT OnHand, Reserved FROM inventory.StockItem WHERE WarehouseId = @w AND Sku = @s", ("@w", warehouse), ("@s", sku))).Single();
        return ((int)row["OnHand"]!, (int)row["Reserved"]!);
    }
}
