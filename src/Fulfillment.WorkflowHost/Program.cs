using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;
using Fulfillment.WorkflowHost;
using Fulfillment.WorkflowHost.Diagnostics;
using Fulfillment.WorkflowHost.Orders;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.WorkflowHost, UsesDaprSidecar: true, DatabaseConnectionName: null));

// Workflow concurrency limits are sidecar settings (dapr/config), not SDK options: the SDK's
// MaxConcurrentWorkflows/MaxConcurrentActivities are obsolete and inert in 1.19.
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterWorkflow<RuntimeProbeWorkflow>(RuntimeProbe.WorkflowName);
    options.RegisterWorkflow<FulfillOrderWorkflow>(FulfillmentWorkflow.Name);
});

var app = builder.Build();
app.MapServiceDefaults();
app.MapWorkflowManagement(WorkflowManagementPolicy.Default);
app.Run();
