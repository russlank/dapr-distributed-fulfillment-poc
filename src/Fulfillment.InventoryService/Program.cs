using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.InventoryService.Diagnostics;
using Fulfillment.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.InventoryService, UsesDaprSidecar: true, DatabaseConnectionName: "InventoryDb"));

var inventoryDb = builder.Configuration.GetConnectionString("InventoryDb")!;
builder.Services.AddSingleton(new ProbeEffectStore(inventoryDb));
builder.Services.AddDaprWorkflow(options => options.RegisterActivity<RecordProbeEffectActivity>(RuntimeProbe.EffectActivity));

var app = builder.Build();
await app.Services.GetRequiredService<ProbeEffectStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<Failpoints>().EnsureStoreAsync();
app.MapServiceDefaults();
app.Run();
