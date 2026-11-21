using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Events;
using Fulfillment.Contracts.Shipping;
using Fulfillment.Messaging.Outbox;
using Fulfillment.ServiceDefaults;
using Fulfillment.ShippingService.Persistence;
using Fulfillment.ShippingService.Shipping;

if (ShippingDbMigrations.IsMigrateCommand(args))
{
    return await ShippingDbMigrations.RunAsync(args);
}

// Dapr app shipping-service: owns ShippingDb, the authority for shipment state and the carrier status history.
// Dispatch to the external carrier by shipment id; carrier status events arrive from the order API's forwarder
// (service invocation) or from Shipping's own poll, through one idempotent ingest function.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.ShippingService, UsesDaprSidecar: true, DatabaseConnectionName: "ShippingDb"));

var shippingDb = builder.Configuration.GetConnectionString("ShippingDb")!;
var shippingOptions = new ShippingOptions();
ShippingOptions.ForEnvironment(shippingOptions, Environment.GetEnvironmentVariable("DAPR_POC_ENV"));
builder.Configuration.GetSection(ShippingOptions.Section).Bind(shippingOptions);
builder.Services.AddSingleton(shippingOptions);
builder.Services.AddHttpClient("carrier", http =>
{
    http.BaseAddress = shippingOptions.CarrierUrl;
    http.Timeout = TimeSpan.FromMinutes(1);
    http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable(CarrierRoutes.ApiKeyVariable) ?? "");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = shippingOptions.RequestTimeout / 2 });
builder.Services.AddSingleton(sp => new ShippingOperations(shippingDb, sp.GetRequiredService<IHttpClientFactory>().CreateClient("carrier"), shippingOptions,
    sp.GetRequiredService<RuntimeIdentity>().Replica, sp.GetRequiredService<ILogger<ShippingOperations>>()));
builder.Services.AddOutboxRelay(builder.Configuration, shippingDb, new OutboxTable(EventSources.Shipping, "shipping.Outbox"),
    sp => Failpoints.Compiled ? sp.GetRequiredService<Failpoints>().HitAsync : (_, _, _) => Task.CompletedTask);
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterActivity<DispatchShipmentActivity>(ShippingActivities.DispatchShipment);
    options.RegisterActivity<GetShipmentStatusActivity>(ShippingActivities.GetShipmentStatus);
    options.RegisterActivity<PollCarrierStatusActivity>(ShippingActivities.PollCarrierStatus);
});

var app = builder.Build();
await ShippingDbMigrations.EnsureCurrentAsync(shippingDb);
await app.Services.GetRequiredService<Failpoints>().EnsureStoreAsync();
app.MapServiceDefaults();
app.MapOutboxEndpoints();

// Reached only through the sidecar (the app token is required on /internal/ routes).
app.MapPost(ShippingRoutes.CarrierStatus, async (CarrierStatusIngest ingest, ShippingOperations operations, CancellationToken ct) =>
    ingest is null || ingest.EventId == Guid.Empty || !CarrierStatuses.IsKnown(ingest.Status) || ingest.Source is not ("callback" or "poll")
        ? Results.BadRequest(new { error = "invalid-carrier-status" })
        : Results.Ok(await operations.IngestAsync(ingest, ct)));
await app.RunAsync();
return 0;
