using Fulfillment.ServiceDefaults;

// External simulator: ordinary HTTP, no Dapr sidecar. M0: identity, health, and CarrierSimDb readiness;
// the simulator contract arrives in M3.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions("carrier-simulator", UsesDaprSidecar: false, DatabaseConnectionName: "CarrierSimDb"));

var app = builder.Build();
app.MapServiceDefaults();
app.Run();
