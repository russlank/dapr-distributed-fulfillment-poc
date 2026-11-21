using Fulfillment.Contracts.Orders;
using Fulfillment.OrderApi.Orders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>
/// The real order API (its Program, DI, endpoints, and EF Core model) hosted in-process against a
/// test database. Two hosts on one database behave like two replicas: separate processes' worth of
/// DI containers, contexts, and connection pools sharing only SQL Server. The Dapr sidecar is absent;
/// order acceptance does not use it.
/// </summary>
public sealed class OrderApiHost(string connectionString, Action<IServiceCollection>? configureServices = null, bool dispatcherEnabled = false)
    : WebApplicationFactory<Program>
{
    public const string CustomerApiKey = "integration-customer-key";
    public const string OperatorApiKey = "integration-operator-key";

    static OrderApiHost()
    {
        // API keys are read from the environment when endpoints are mapped (same value for every host).
        Environment.SetEnvironmentVariable(OrderEndpoints.ApiKeyVariable, CustomerApiKey);
        Environment.SetEnvironmentVariable("ADMIN_API_KEY", "integration-admin-key");
        Environment.SetEnvironmentVariable("OPERATOR_API_KEY", OperatorApiKey);
    }

    public HttpClient CreateOperatorClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(OrderRoutes.ApiKeyHeader, OperatorApiKey);
        return client;
    }

    public HttpClient CreateCustomerClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(OrderRoutes.ApiKeyHeader, CustomerApiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("ConnectionStrings:OrderDb", connectionString);
        builder.UseSetting("REPLICA_NAME", "integration-test");
        // Warnings and errors only: the Dapr workflow worker retries its sidecar connection in tests.
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Dapr", "Error");
        builder.UseSetting("Logging:LogLevel:Grpc", "Error");
        // The start dispatcher runs only in tests that enable it (it would act on every pending row).
        builder.UseSetting("StartDispatcher:Enabled", dispatcherEnabled ? "true" : "false");
        builder.UseSetting("SignalDispatcher:Enabled", "false");
        // Tests drive the carrier forwarder and the repair dispatcher themselves; background instances would race
        // them for the same rows (and hold leases while calling a sidecar that does not exist in tests).
        builder.UseSetting("CarrierForwarder:Enabled", "false");
        builder.UseSetting("RepairDispatcher:Enabled", "false");
        builder.UseSetting("OutboxRelay:Enabled", "false");
        builder.UseSetting("Reconciler:Enabled", "false");
        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }
}
