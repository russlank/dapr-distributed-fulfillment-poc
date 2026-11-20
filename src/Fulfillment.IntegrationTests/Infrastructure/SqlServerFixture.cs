extern alias inventory;
extern alias provider;

using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>
/// One SQL Server for the test run: a container from the same pinned image as the Compose stack, or
/// the server in FULFILLMENT_TEST_SQL_SERVER (a master-database connection string, for CI service
/// containers). Every test class creates its own database, so classes run in parallel without
/// sharing rows.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ServerVariable = "FULFILLMENT_TEST_SQL_SERVER";
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    private MsSqlContainer? container;

    public string MasterConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            MasterConnectionString = external;
        }
        else
        {
            container = new MsSqlBuilder(Image).Build();
            await container.StartAsync();
            MasterConnectionString = container.GetConnectionString();
        }

        await WaitForServerAsync(TimeSpan.FromSeconds(120));
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty database configured like OrderDb (read committed snapshot on).</summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 33, 100)];
        await using (var connection = new SqlConnection(MasterConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON;";
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Creates a database and applies the OrderDb migrations, as the migration job does.</summary>
    public async Task<string> CreateOrderDbAsync(string prefix)
    {
        var connectionString = await CreateDatabaseAsync(prefix);
        await MigrateAsync(connectionString);
        return connectionString;
    }

    /// <summary>Creates a database and applies the InventoryDb migrations, as the inventory migration job does.</summary>
    public async Task<string> CreateProviderDbAsync(string prefix)
    {
        var connectionString = await CreateDatabaseAsync(prefix);
        var options = new DbContextOptionsBuilder<provider::Fulfillment.PaymentProviderSimulator.Persistence.ProviderDbContext>();
        provider::Fulfillment.PaymentProviderSimulator.Persistence.ProviderDbContext.Configure(options, connectionString);
        await using var db = new provider::Fulfillment.PaymentProviderSimulator.Persistence.ProviderDbContext(options.Options);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public async Task<string> CreateInventoryDbAsync(string prefix)
    {
        var connectionString = await CreateDatabaseAsync(prefix);
        var options = new DbContextOptionsBuilder<inventory::Fulfillment.InventoryService.Persistence.InventoryDbContext>();
        inventory::Fulfillment.InventoryService.Persistence.InventoryDbContext.Configure(options, connectionString);
        await using var db = new inventory::Fulfillment.InventoryService.Persistence.InventoryDbContext(options.Options);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>();
        OrderDbContext.Configure(options, connectionString);
        await using var db = new OrderDbContext(options.Options);
        await db.Database.MigrateAsync();
    }

    private async Task WaitForServerAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(MasterConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
