using Fulfillment.CarrierSimulator.Carrier;
using Fulfillment.CarrierSimulator.Persistence;
using Fulfillment.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

// External simulator: ordinary HTTP, no Dapr sidecar. It plays the carrier: accepts shipments by shipment id
// (stored with their callback schedule before answering), answers status queries, and sends signed status
// callbacks to the order API's ingress, retried and logged.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions("carrier-simulator", UsesDaprSidecar: false, DatabaseConnectionName: "CarrierSimDb"));

var connectionString = builder.Configuration.GetConnectionString("CarrierSimDb")
    ?? throw new InvalidOperationException("connection string CarrierSimDb is not configured");
var carrierOptions = new CarrierOptions();
CarrierOptions.ForEnvironment(carrierOptions, Environment.GetEnvironmentVariable("DAPR_POC_ENV"));
builder.Configuration.GetSection(CarrierOptions.Section).Bind(carrierOptions);
builder.Services.AddSingleton(carrierOptions);
builder.Services.AddSingleton(new CarrierOperations(connectionString, carrierOptions));
builder.Services.AddHttpClient("callbacks");
builder.Services.AddHostedService(sp => new CallbackSender(connectionString, carrierOptions, sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<CallbackSender>>()));

var app = builder.Build();

// A single instance owns this database, so it applies its own schema at start.
var dbOptions = new DbContextOptionsBuilder<CarrierDbContext>();
CarrierDbContext.Configure(dbOptions, connectionString);
await using (var db = new CarrierDbContext(dbOptions.Options))
{
    await db.Database.MigrateAsync();
}

await app.Services.GetRequiredService<Failpoints>().EnsureStoreAsync();
app.MapServiceDefaults();
app.MapCarrierEndpoints();
await app.RunAsync();
