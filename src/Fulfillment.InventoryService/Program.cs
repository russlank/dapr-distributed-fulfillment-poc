using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Contracts.Events;
using Fulfillment.Contracts.Workflows;
using Fulfillment.InventoryService.Diagnostics;
using Fulfillment.InventoryService.Inventory;
using Fulfillment.InventoryService.Persistence;
using Fulfillment.Messaging.Outbox;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Options;

if (InventoryDbMigrations.IsMigrateCommand(args))
{
    return await InventoryDbMigrations.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.InventoryService, UsesDaprSidecar: true, DatabaseConnectionName: "InventoryDb"));

var inventoryDb = builder.Configuration.GetConnectionString("InventoryDb")!;
builder.Services.AddSingleton(new ProbeEffectStore(inventoryDb));
builder.Services.AddOptions<InventoryOptions>()
    .Configure(o => InventoryOptions.ForEnvironment(o, Environment.GetEnvironmentVariable("DAPR_POC_ENV"), Environment.GetEnvironmentVariable("INVENTORY_HOLD_TTL")))
    .Bind(builder.Configuration.GetSection(InventoryOptions.Section));
builder.Services.AddSingleton(sp => new InventoryOperations(inventoryDb, sp.GetRequiredService<RuntimeIdentity>().Replica));
builder.Services.AddSingleton<IStockCoordinator>(sp =>
    sp.GetRequiredService<IOptions<InventoryOptions>>().Value.Coordination == InventoryCoordination.SqlOnly
        ? new SqlCoordinator(sp.GetRequiredService<InventoryOperations>())
        : new ActorCoordinator());
builder.Services.AddSingleton(sp => new HoldSweeper(sp.GetRequiredService<InventoryOperations>(), sp.GetRequiredService<IOptions<InventoryOptions>>(),
    sp.GetRequiredService<RuntimeIdentity>(), inventoryDb, sp.GetRequiredService<ILogger<HoldSweeper>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HoldSweeper>());
builder.Services.AddOutboxRelay(builder.Configuration, inventoryDb, new OutboxTable(EventSources.Inventory, "inventory.Outbox"),
    sp => Failpoints.Compiled ? sp.GetRequiredService<Failpoints>().HitAsync : (_, _, _) => Task.CompletedTask);
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterActivity<RecordProbeEffectActivity>(RuntimeProbe.EffectActivity);
    options.RegisterActivity<ReserveOrderLineActivity>(InventoryActivities.ReserveOrderLine);
    options.RegisterActivity<ReleaseOrderHoldsActivity>(InventoryActivities.ReleaseOrderHolds);
    options.RegisterActivity<ConsumeOrderHoldsActivity>(InventoryActivities.ConsumeOrderHolds);
    options.RegisterActivity<ExtendOrderHoldsActivity>(InventoryActivities.ExtendOrderHolds);
});
// Non-reentrant (the default): one turn per (warehouse, SKU) at a time.
builder.Services.AddActors(options => options.Actors.RegisterActor<StockActor>());

var app = builder.Build();
await InventoryDbMigrations.EnsureCurrentAsync(inventoryDb);
await app.Services.GetRequiredService<ProbeEffectStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<Failpoints>().EnsureStoreAsync();
app.MapServiceDefaults();
app.MapActorsHandlers();
app.MapOutboxEndpoints();
await app.RunAsync();
return 0;
