using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.InventoryService.Persistence;

/// <summary>
/// InventoryDb schema changes run as a one-shot step (`Fulfillment.InventoryService migrate`, the
/// inventory-db-migrate job in Compose), never at app start: three replicas starting together must
/// not race on DDL. The app checks that the schema is current and refuses to start otherwise.
/// </summary>
public static partial class InventoryDbMigrations
{
    public const string Command = "migrate";
    public const string ConnectionName = "InventoryDb";

    public static bool IsMigrateCommand(string[] args) => args is [Command];

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"connection string {ConnectionName} is not configured");
        builder.Services.AddDbContext<InventoryDbContext>(o => InventoryDbContext.Configure(o, connectionString));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(InventoryDbMigrations));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        var pendingNames = string.Join(", ", pending);
        Log.Pending(logger, pending.Count, pendingNames);
        await db.Database.MigrateAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var latest = applied.LastOrDefault();
        Log.Current(logger, applied.Count, latest);
        return 0;
    }

    public static async Task EnsureCurrentAsync(string connectionString, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>();
        InventoryDbContext.Configure(options, connectionString);
        await using var db = new InventoryDbContext(options.Options);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"InventoryDb has {pending.Count} pending migration(s) ({string.Join(", ", pending)}); run the migrate command first");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "InventoryDb migrations pending: {Count} ({Migrations})")]
        public static partial void Pending(ILogger logger, int count, string migrations);

        [LoggerMessage(Level = LogLevel.Information, Message = "InventoryDb schema current: {Count} migrations applied, latest {Latest}")]
        public static partial void Current(ILogger logger, int count, string? latest);
    }
}

/// <summary>Design-time factory for `dotnet ef migrations add` (no connection is opened).</summary>
public sealed class InventoryDbDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<InventoryDbContext>();
        InventoryDbContext.Configure(builder, "Server=design-time;Database=InventoryDb;Integrated Security=false");
        return new InventoryDbContext(builder.Options);
    }
}
