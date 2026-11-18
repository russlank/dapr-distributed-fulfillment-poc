using Dapr.Workflow;
using Fulfillment.ComputeWorker.Diagnostics;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.ComputeWorker, UsesDaprSidecar: true, DatabaseConnectionName: null));
builder.Services.AddDaprWorkflow(options => options.RegisterActivity<ComputeProbeActivity>(RuntimeProbe.ComputeActivity));

var app = builder.Build();
app.MapServiceDefaults();
app.Run();
