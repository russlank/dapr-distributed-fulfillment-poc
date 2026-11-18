using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.OrderApi.Persistence;

/// <summary>
/// Schema changes run as a separate one-shot step (`Fulfillment.OrderApi migrate`, the
/// order-db-migrate job in Compose), never implicitly at app start: replicas starting together must
/// not race on DDL, and a failed migration must stop the rollout instead of leaving a half-started
/// app. EF Core takes an application lock while migrating, so a repeated or concurrent run is safe.
/// The app itself only checks that the schema is current and refuses to start otherwise.
/// </summary>
public static partial class OrderDbMigrations
{
    public const string Command = "migrate";
    public const string ConnectionName = "OrderDb";

    public static bool IsMigrateCommand(string[] args) => args is [Command];

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"connection string {ConnectionName} is not configured");
        builder.Services.AddDbContext<OrderDbContext>(o => OrderDbContext.Configure(o, connectionString));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OrderDbMigrations));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        var pendingNames = string.Join(", ", pending);
        Log.Pending(logger, pending.Count, pendingNames);
        await db.Database.MigrateAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var latest = applied.LastOrDefault();
        Log.Current(logger, applied.Count, latest);
        return 0;
    }

    /// <summary>Throws when migrations are pending, so an app never runs against an older schema.</summary>
    public static async Task EnsureCurrentAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"OrderDb has {pending.Count} pending migration(s) ({string.Join(", ", pending)}); run the migrate command first");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "OrderDb migrations pending: {Count} ({Migrations})")]
        public static partial void Pending(ILogger logger, int count, string migrations);

        [LoggerMessage(Level = LogLevel.Information, Message = "OrderDb schema current: {Count} migrations applied, latest {Latest}")]
        public static partial void Current(ILogger logger, int count, string? latest);
    }
}

/// <summary>Design-time factory for `dotnet ef migrations add` (no connection is opened).</summary>
public sealed class OrderDbDesignTimeFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<OrderDbContext>();
        OrderDbContext.Configure(builder, "Server=design-time;Database=OrderDb;Integrated Security=false");
        return new OrderDbContext(builder.Options);
    }
}
