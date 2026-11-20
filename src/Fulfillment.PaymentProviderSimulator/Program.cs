using Fulfillment.PaymentProviderSimulator.Persistence;
using Fulfillment.PaymentProviderSimulator.Provider;
using Fulfillment.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

// External simulator: ordinary HTTP, no Dapr sidecar. It plays the payment provider: authorize, capture, void,
// and refund by the caller's operation key, a status query, business rules, and persisted deterministic faults.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions("payment-provider-simulator", UsesDaprSidecar: false, DatabaseConnectionName: "PaymentProviderSimDb"));

var connectionString = builder.Configuration.GetConnectionString("PaymentProviderSimDb")
    ?? throw new InvalidOperationException("connection string PaymentProviderSimDb is not configured");
var providerOptions = new ProviderOptions();
ProviderOptions.ForEnvironment(providerOptions, Environment.GetEnvironmentVariable("DAPR_POC_ENV"));
builder.Configuration.GetSection(ProviderOptions.Section).Bind(providerOptions);
builder.Services.AddSingleton(providerOptions);
builder.Services.AddSingleton(new ProviderOperations(connectionString, providerOptions));

var app = builder.Build();

// A single instance owns this database, so it applies its own schema at start (the services, which run
// several replicas, use a one-shot migrate job instead).
var dbOptions = new DbContextOptionsBuilder<ProviderDbContext>();
ProviderDbContext.Configure(dbOptions, connectionString);
await using (var db = new ProviderDbContext(dbOptions.Options))
{
    await db.Database.MigrateAsync();
}

app.MapServiceDefaults();
app.MapProviderEndpoints();
await app.RunAsync();
