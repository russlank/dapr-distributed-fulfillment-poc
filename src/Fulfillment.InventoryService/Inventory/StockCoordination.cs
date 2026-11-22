using Dapr.Actors;
using Dapr.Actors.Client;
using Dapr.Actors.Runtime;
using Fulfillment.ServiceDefaults;

namespace Fulfillment.InventoryService.Inventory;

public sealed class InventoryOptions
{
    public const string Section = "Inventory";

    /// <summary>Actor (default): reserve and release go through the warehouse/SKU StockActor; SqlOnly: straight to the guarded SQL.</summary>
    public string Coordination { get; set; } = InventoryCoordination.Actor;

    /// <summary>How long a hold stays valid (SQL time). Drill environment: 60 s.</summary>
    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Holds expired per sweep at most (one transaction each).</summary>
    public int SweepBatch { get; set; } = 50;

    /// <summary>How long the sweeper's lease lasts; another replica takes over after it.</summary>
    public TimeSpan SweeperLease { get; set; } = TimeSpan.FromSeconds(15);

    public bool SweeperEnabled { get; set; } = true;

    /// <summary>A hold still Held this long after its expiry was not swept in time (metric <c>holds_overdue</c>, alert ReservationHoldsLeaking).</summary>
    public TimeSpan HoldOverdueGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Defaults of the environment; INVENTORY_HOLD_TTL (a TimeSpan, e.g. 00:00:20), when set, overrides the TTL (drills lower it per run).</summary>
    public static void ForEnvironment(InventoryOptions options, string? environment, string? holdTtl = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(environment, "drill", StringComparison.Ordinal))
        {
            options.HoldTtl = TimeSpan.FromSeconds(60);
            options.SweepInterval = TimeSpan.FromSeconds(2);
            options.SweeperLease = TimeSpan.FromSeconds(6);
            options.HoldOverdueGrace = TimeSpan.FromSeconds(15);
        }

        if (!string.IsNullOrWhiteSpace(holdTtl))
        {
            options.HoldTtl = TimeSpan.Parse(holdTtl, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

public static class InventoryCoordination
{
    public const string Actor = "Actor";
    public const string SqlOnly = "SqlOnly";
}

/// <summary>Who serializes reserve and release for one warehouse and SKU. The SQL guard decides either way.</summary>
public interface IStockCoordinator
{
    Task<ReserveResult> ReserveAsync(ReserveCommand command, CancellationToken ct);

    Task<ReleaseResult> ReleaseAsync(string warehouseId, string sku, ReleaseCommand command, CancellationToken ct);
}

/// <summary>SqlOnly: no actor; concurrent requests meet in SQL Server (row locks and the guard).</summary>
public sealed class SqlCoordinator(InventoryOperations operations) : IStockCoordinator
{
    public Task<ReserveResult> ReserveAsync(ReserveCommand command, CancellationToken ct) => operations.ReserveAsync(command, ct);

    public Task<ReleaseResult> ReleaseAsync(string warehouseId, string sku, ReleaseCommand command, CancellationToken ct) => operations.ReleaseAsync(command, ct);
}

/// <summary>Actor: each request is a turn of the StockActor for (warehouse, SKU), wherever Dapr placed it.</summary>
public sealed class ActorCoordinator : IStockCoordinator
{
    public Task<ReserveResult> ReserveAsync(ReserveCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Proxy(command.WarehouseId, command.Sku).InvokeMethodAsync<ReserveCommand, ReserveResult>(nameof(IStockActor.ReserveAsync), command, ct);
    }

    public Task<ReleaseResult> ReleaseAsync(string warehouseId, string sku, ReleaseCommand command, CancellationToken ct) =>
        Proxy(warehouseId, sku).InvokeMethodAsync<ReleaseCommand, ReleaseResult>(nameof(IStockActor.ReleaseAsync), command, ct);

    public static string ActorIdOf(string warehouseId, string sku) => $"{warehouseId}|{sku}";

    private static ActorProxy Proxy(string warehouseId, string sku) => ActorProxy.Create(new ActorId(ActorIdOf(warehouseId, sku)), nameof(StockActor));
}

public interface IStockActor : IActor
{
    Task<ReserveResult> ReserveAsync(ReserveCommand command);

    Task<ReleaseResult> ReleaseAsync(ReleaseCommand command);
}

/// <summary>
/// Failpoint names inside the stock actor's turn (drill builds only). The key is the actor id
/// (warehouse|SKU), so a drill can make every reservation of one SKU fail without knowing order ids.
/// </summary>
public static class StockActorFailpoints
{
    /// <summary>Inside the reserve turn, before the SQL: Throw makes the reservation fail, Delay holds the turn.</summary>
    public const string BeforeStockReserve = "BeforeStockReserve";
}

/// <summary>
/// Serializes reserve and release for one (warehouse, SKU): one turn at a time, non-reentrant. It
/// keeps no state and holds no authority: every turn runs the guarded SQL, which alone decides.
/// Its activation is logged with the replica, which is how drills find the actor's host.
/// </summary>
public sealed partial class StockActor(ActorHost host, InventoryOperations operations, Failpoints failpoints, RuntimeIdentity identity, ILogger<StockActor> logger)
    : Actor(host), IStockActor
{
    public async Task<ReserveResult> ReserveAsync(ReserveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        await failpoints.HitAsync(StockActorFailpoints.BeforeStockReserve, Id.GetId());
        return await operations.ReserveAsync(command);
    }

    public Task<ReleaseResult> ReleaseAsync(ReleaseCommand command) => operations.ReleaseAsync(command);

    protected override Task OnActivateAsync()
    {
        var id = Id.GetId();
        LogActivated(logger, id, identity.Replica);
        return Task.CompletedTask;
    }

    protected override Task OnDeactivateAsync()
    {
        var id = Id.GetId();
        LogDeactivated(logger, id, identity.Replica);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "stock actor {ActorId} activated on {Replica}")]
    private static partial void LogActivated(ILogger logger, string actorId, string replica);

    [LoggerMessage(Level = LogLevel.Information, Message = "stock actor {ActorId} deactivated on {Replica}")]
    private static partial void LogDeactivated(ILogger logger, string actorId, string replica);
}
