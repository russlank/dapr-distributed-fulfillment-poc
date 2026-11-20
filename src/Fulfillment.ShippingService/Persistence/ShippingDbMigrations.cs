using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.ShippingService.Persistence;

/// <summary>
/// ShippingDb schema changes run as a one-shot step (`Fulfillment.ShippingService migrate`, the
/// shipping-db-migrate job in Compose), never at app start: replicas starting together must
/// not race on DDL. The app checks that the schema is current and refuses to start otherwise.
/// </summary>
public static partial class ShippingDbMigrations
{
    public const string Command = "migrate";
    public const string ConnectionName = "ShippingDb";

    public static bool IsMigrateCommand(string[] args) => args is [Command];

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"connection string {ConnectionName} is not configured");
        builder.Services.AddDbContext<ShippingDbContext>(o => ShippingDbContext.Configure(o, connectionString));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ShippingDbMigrations));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
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
        var options = new DbContextOptionsBuilder<ShippingDbContext>();
        ShippingDbContext.Configure(options, connectionString);
        await using var db = new ShippingDbContext(options.Options);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"ShippingDb has {pending.Count} pending migration(s) ({string.Join(", ", pending)}); run the migrate command first");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "ShippingDb migrations pending: {Count} ({Migrations})")]
        public static partial void Pending(ILogger logger, int count, string migrations);

        [LoggerMessage(Level = LogLevel.Information, Message = "ShippingDb schema current: {Count} migrations applied, latest {Latest}")]
        public static partial void Current(ILogger logger, int count, string? latest);
    }
}
