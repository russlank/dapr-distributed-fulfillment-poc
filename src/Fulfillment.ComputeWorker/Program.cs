using Dapr.Workflow;
using Fulfillment.ComputeWorker.Checks;
using Fulfillment.ComputeWorker.Diagnostics;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.ComputeWorker, UsesDaprSidecar: true, DatabaseConnectionName: null));
builder.Services.Configure<ComputeCheckOptions>(builder.Configuration.GetSection(ComputeCheckOptions.Section));
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterActivity<ComputeProbeActivity>(RuntimeProbe.ComputeActivity);
    options.RegisterActivity<PriceOrderActivity>(FulfillmentWorkflow.PriceOrderActivity);
    options.RegisterActivity<AssessOrderRiskActivity>(FulfillmentWorkflow.AssessOrderRiskActivity);
});

var app = builder.Build();
app.MapServiceDefaults();
app.Run();
