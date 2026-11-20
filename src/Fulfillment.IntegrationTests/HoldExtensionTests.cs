extern alias inventory;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using inventory::Fulfillment.InventoryService.Inventory;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The extension of an order's holds before the payment capture, against SQL Server: all holds or none, capped by
/// the authorization's expiry, never shortened, refused when the holds cannot last the required time, replayed with
/// the current expiry, and serialized with the sweeper (a hold is never both extended and expired).
/// </summary>
public sealed class HoldExtensionTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private InventoryOperations ops = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateInventoryDbAsync("extend");
        ops = new InventoryOperations(db, "test-replica");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> HoldTwoLinesAsync(string skuPrefix, TimeSpan ttl)
    {
        await Stock.SetAsync(db, "W1", skuPrefix + "A", 5);
        await Stock.SetAsync(db, "W1", skuPrefix + "B", 5);
        var order = Guid.NewGuid();
        foreach (var (line, sku) in new[] { (1, skuPrefix + "A"), (2, skuPrefix + "B") })
        {
            var held = await ops.ReserveAsync(new ReserveCommand(order, line, sku, 1, "W1", InventoryKeys.Reserve(order, line, "W1"), ttl), Ct);
            Assert.Equal(InventoryOutcomes.Reserved, held.Outcome);
        }

        return order;
    }

    private Task<long> RemainingMsAsync(Guid order) =>
        Db.ScalarAsync<long>(db, "SELECT MIN(DATEDIFF_BIG(millisecond, SYSUTCDATETIME(), ExpiresAt)) FROM inventory.Hold WHERE OrderId = @o", ("@o", order));

    [Fact]
    public async Task Extend_AllHeld_ExtendsEveryHoldOnceWithOneEventEach()
    {
        var order = await HoldTwoLinesAsync("SKU-E1", TimeSpan.FromSeconds(30));

        var result = await ops.ExtendOrderAsync(order, 2, TimeSpan.FromMinutes(30), null, TimeSpan.FromMinutes(1), InventoryKeys.Extend(order), Ct);

        Assert.Equal((InventoryOutcomes.Extended, false), (result.Outcome, result.Replayed));
        Assert.InRange(await RemainingMsAsync(order), 29 * 60_000, 30 * 60_000 + 2_000);
        Assert.Equal(2, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId = @o AND EventType = 'HoldExtended'", ("@o", order)));
        // The outbox stays gapless (2 creations + 2 extensions).
        Assert.Equal(4, await Db.ScalarAsync<int>(db, "SELECT MAX(Sequence) FROM inventory.Outbox WHERE OrderId = @o", ("@o", order)));
    }

    [Fact]
    public async Task Extend_IsCappedByTheAuthorizationExpiry_AndNeverShortensAHold()
    {
        var capped = await HoldTwoLinesAsync("SKU-E2", TimeSpan.FromSeconds(30));
        var authExpires = await Db.ScalarAsync<DateTime>(db, "SELECT DATEADD(minute, 5, SYSUTCDATETIME())");
        await ops.ExtendOrderAsync(capped, 2, TimeSpan.FromMinutes(30), DateTime.SpecifyKind(authExpires, DateTimeKind.Utc), TimeSpan.FromMinutes(1), InventoryKeys.Extend(capped), Ct);
        Assert.InRange(await RemainingMsAsync(capped), 4 * 60_000, 5 * 60_000 + 2_000);

        var longer = await HoldTwoLinesAsync("SKU-E3", TimeSpan.FromMinutes(20));
        var result = await ops.ExtendOrderAsync(longer, 2, TimeSpan.FromSeconds(30), null, TimeSpan.FromSeconds(10), InventoryKeys.Extend(longer), Ct);

        Assert.Equal(InventoryOutcomes.Extended, result.Outcome);
        Assert.InRange(await RemainingMsAsync(longer), 19 * 60_000, 20 * 60_000 + 2_000);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId = @o AND EventType = 'HoldExtended'", ("@o", longer)));
    }

    [Fact]
    public async Task Extend_OneHoldExpired_ChangesNothing()
    {
        var order = await HoldTwoLinesAsync("SKU-E4", TimeSpan.FromMinutes(5));
        await Db.ExecuteAsync(db, "UPDATE inventory.Hold SET ExpiresAt = DATEADD(millisecond, 10, CreatedAt) WHERE OrderId = @o AND LineNumber = 2", ("@o", order));
        await Task.Delay(200, Ct);

        var result = await ops.ExtendOrderAsync(order, 2, TimeSpan.FromMinutes(30), null, TimeSpan.FromSeconds(10), InventoryKeys.Extend(order), Ct);

        Assert.Equal(InventoryOutcomes.HoldExpired, result.Outcome);
        Assert.InRange(await Db.ScalarAsync<long>(db, "SELECT DATEDIFF_BIG(millisecond, SYSUTCDATETIME(), ExpiresAt) FROM inventory.Hold WHERE OrderId = @o AND LineNumber = 1", ("@o", order)),
            4 * 60_000, 5 * 60_000 + 2_000);
    }

    [Fact]
    public async Task Extend_AuthorizationExpiresTooSoon_IsInsufficientValidityAndChangesNothing()
    {
        var order = await HoldTwoLinesAsync("SKU-E5", TimeSpan.FromSeconds(20));
        var authExpires = await Db.ScalarAsync<DateTime>(db, "SELECT DATEADD(second, 5, SYSUTCDATETIME())");

        var result = await ops.ExtendOrderAsync(order, 2, TimeSpan.FromMinutes(30), DateTime.SpecifyKind(authExpires, DateTimeKind.Utc), TimeSpan.FromMinutes(1),
            InventoryKeys.Extend(order), Ct);

        Assert.Equal(InventoryOutcomes.InsufficientValidity, result.Outcome);
        Assert.InRange(await RemainingMsAsync(order), 10_000, 21_000);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId = @o AND EventType = 'HoldExtended'", ("@o", order)));
    }

    [Fact]
    public async Task Extend_Repeated_ReturnsTheStoredOutcomeWithTheCurrentExpiry()
    {
        var order = await HoldTwoLinesAsync("SKU-E6", TimeSpan.FromSeconds(30));
        await ops.ExtendOrderAsync(order, 2, TimeSpan.FromMinutes(30), null, TimeSpan.FromMinutes(1), InventoryKeys.Extend(order), Ct);
        await Db.ExecuteAsync(db, "UPDATE inventory.Hold SET ExpiresAt = DATEADD(second, 3, SYSUTCDATETIME()) WHERE OrderId = @o", ("@o", order));

        var replay = await ops.ExtendOrderAsync(order, 2, TimeSpan.FromMinutes(30), null, TimeSpan.FromMinutes(1), InventoryKeys.Extend(order), Ct);

        // A replay never claims validity that is gone: the caller checks the remaining time.
        Assert.Equal((InventoryOutcomes.Extended, true), (replay.Outcome, replay.Replayed));
        Assert.InRange(replay.RemainingMilliseconds, 0, 3_500);
        Assert.Equal(2, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM inventory.Outbox WHERE OrderId = @o AND EventType = 'HoldExtended'", ("@o", order)));
    }

    [Fact]
    public async Task Extend_RacingTheSweeper_AHoldIsNeverBothExtendedAndExpired()
    {
        for (var round = 0; round < 10; round++)
        {
            await Stock.SetAsync(db, "W1", "SKU-E7", 4);
            var order = Guid.NewGuid();
            var hold = await ops.ReserveAsync(new ReserveCommand(order, 1, "SKU-E7", 1, "W1", InventoryKeys.Reserve(order, 1, "W1"), TimeSpan.FromMilliseconds(150)), Ct);
            await Task.Delay(100, Ct);

            var extend = Task.Run(() => ops.ExtendOrderAsync(order, 1, TimeSpan.FromMinutes(30), null, TimeSpan.FromSeconds(1), InventoryKeys.Extend(order), Ct), Ct);
            var sweep = Task.Run(async () =>
            {
                await Task.Delay(60, Ct);
                return await ops.ExpireNextDueAsync(Ct);
            }, Ct);
            var extended = (await extend).Outcome == InventoryOutcomes.Extended;
            var expired = await sweep == hold.HoldId;

            var state = await Db.ScalarAsync<string>(db, "SELECT State FROM inventory.Hold WHERE HoldId = @h", ("@h", hold.HoldId!.Value));
            Assert.False(extended && expired, $"round {round}: extended and expired");
            Assert.Equal(extended ? "Held" : "Expired", state is "Held" or "Expired" ? state : "?");
        }
    }
}
