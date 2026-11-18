using Fulfillment.ServiceDefaults;

// External simulator: ordinary HTTP, no Dapr sidecar. M0: identity, health, and PaymentProviderSimDb readiness;
// the simulator contract arrives in M3.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions("payment-provider-simulator", UsesDaprSidecar: false, DatabaseConnectionName: "PaymentProviderSimDb"));

var app = builder.Build();
app.MapServiceDefaults();
app.Run();
