using Fulfillment.Contracts;
using Fulfillment.ServiceDefaults;

// M0: host, sidecar, identity, health, and PaymentDb readiness only; business activities arrive in M3.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.PaymentService, UsesDaprSidecar: true, DatabaseConnectionName: "PaymentDb"));

var app = builder.Build();
app.MapServiceDefaults();
app.Run();
