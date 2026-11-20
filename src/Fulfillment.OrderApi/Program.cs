using System.Text.RegularExpressions;
using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Messaging;
using Fulfillment.OrderApi;
using Fulfillment.OrderApi.Diagnostics;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.OrderApi.Orders;
using Fulfillment.OrderApi.Persistence;
using Fulfillment.OrderApi.Reviews;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Options;

// One-shot schema step (Compose job order-db-migrate); the app never migrates at start.
if (OrderDbMigrations.IsMigrateCommand(args))
{
    return await OrderDbMigrations.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.OrderApi, UsesDaprSidecar: true, DatabaseConnectionName: "OrderDb"));

var orderDb = builder.Configuration.GetConnectionString("OrderDb")!;
builder.Services.AddSingleton(new ProbeStore(orderDb));
builder.Services.AddDbContext<OrderDbContext>(o => OrderDbContext.Configure(o, orderDb));
builder.Services.AddScoped<OrderAcceptance>();
builder.Services.AddSingleton(new ExecutionFence(orderDb));
// Fulfillment steps; their transaction checkpoints are failpoints in drill builds (no-ops otherwise).
builder.Services.AddSingleton(sp =>
{
    return new FulfillmentSteps(orderDb, sp.GetRequiredService<RuntimeIdentity>().Replica, Checkpoints(sp));
});
builder.Services.AddSingleton(sp => new OrderLifecycleSteps(orderDb, Checkpoints(sp)));

// Reviews: decisions (operator key), review steps, and the signal dispatcher that wakes workflows
// through the workflow host's management API. Review timing follows the environment's time profile.
builder.Services.AddOptions<ReviewOptions>()
    .Configure(o => ReviewOptions.ForEnvironment(o, Environment.GetEnvironmentVariable("DAPR_POC_ENV")))
    .Bind(builder.Configuration.GetSection(ReviewOptions.Section));
builder.Services.AddSingleton(sp => new ReviewDecisions(orderDb, Checkpoints(sp)));
builder.Services.AddSingleton(sp => new ReviewSteps(orderDb, sp.GetRequiredService<IOptions<ReviewOptions>>().Value, Checkpoints(sp)));
builder.Services.Configure<SignalDispatcherOptions>(builder.Configuration.GetSection(SignalDispatcherOptions.Section));
builder.Services.AddSingleton(new SignalStore(orderDb));
builder.Services.AddTransient<IWorkflowSignaler, WorkflowHostSignaler>();
builder.Services.AddHostedService<SignalDispatcher>();

// Durable start dispatcher: starts fulfillment workflows through the workflow host's management API.
builder.Services.Configure<StartDispatcherOptions>(builder.Configuration.GetSection(StartDispatcherOptions.Section));
builder.Services.AddSingleton(new WorkflowStartStore(orderDb));
builder.Services.AddTransient<IWorkflowStarter, WorkflowHostStarter>();
builder.Services.AddHostedService<WorkflowStartDispatcher>();
builder.Services.AddSidecarInvoker();
builder.Services.AddTransient<WorkflowHostClient>();

// The order API hosts the activities that write OrderDb; workflows themselves run on the workflow host.
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterActivity<ClaimProbeExecutionActivity>(ActivityNames.Claim);
    options.RegisterActivity<CompleteProbeExecutionActivity>(ActivityNames.Complete);
    options.RegisterActivity<ClaimFulfillmentExecutionActivity>(FulfillmentWorkflow.ClaimExecutionActivity);
    options.RegisterActivity<ValidateFulfillmentOrderActivity>(FulfillmentWorkflow.ValidateOrderActivity);
    options.RegisterActivity<RecordFulfillmentChecksActivity>(FulfillmentWorkflow.RecordChecksActivity);
    options.RegisterActivity<ApplyStubFulfillmentStepActivity>(FulfillmentWorkflow.ApplyStubStepActivity);
    options.RegisterActivity<CompleteStubFulfillmentActivity>(FulfillmentWorkflow.CompleteStubFulfillmentActivity);
    options.RegisterActivity<CloseUnfulfilledOrderActivity>(FulfillmentWorkflow.CloseUnfulfilledOrderActivity);
    options.RegisterActivity<BeginStubFulfillmentActivity>(FulfillmentWorkflow.BeginFulfillmentActivity);
    options.RegisterActivity<CreateReviewTaskActivity>(FulfillmentWorkflow.CreateReviewTaskActivity);
    options.RegisterActivity<ResolveReviewActivity>(FulfillmentWorkflow.ResolveReviewActivity);
    options.RegisterActivity<ExpireOrEscalateReviewActivity>(FulfillmentWorkflow.ExpireOrEscalateReviewActivity);
    options.RegisterActivity<AdvanceFulfillmentActivity>(OrderLifecycleActivities.AdvanceFulfillment);
    options.RegisterActivity<BeginDispatchActivity>(OrderLifecycleActivities.BeginDispatch);
    options.RegisterActivity<OpenInterventionCaseActivity>(OrderLifecycleActivities.OpenInterventionCase);
    options.RegisterActivity<ReadInterventionCaseActivity>(OrderLifecycleActivities.ReadInterventionCase);
});

var app = builder.Build();
await OrderDbMigrations.EnsureCurrentAsync(app.Services);
await app.Services.GetRequiredService<ProbeStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<Failpoints>().EnsureStoreAsync();
app.MapServiceDefaults();
// Every answer names the replica that produced it, so clients and scenarios can prove which
// replica served a request instead of inferring it from the URL they used.
var servedBy = app.Services.GetRequiredService<RuntimeIdentity>().Replica;
app.Use((context, next) =>
{
    context.Response.Headers[OrderRoutes.ServedByHeader] = servedBy;
    return next(context);
});

// Drill builds: the AbortConnection failpoint action resets the client's connection instead of
// answering (the work done before it stays committed). Never raised in release builds.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (FailpointAbortException)
    {
        context.Abort();
    }
});
app.MapOrders();
app.MapReviews();
app.MapOrderExecutionDiagnostics();

// Runtime probe (operational diagnostic, admin key): exercises execution claim, cross-app
// activities, durable timer, and keyed effects end to end. Not business functionality.
var probes = app.MapGroup("/system/probes/runtime").RequireApiKey("ADMIN_API_KEY");

probes.MapPost("/{probeId}", async (string probeId, ProbeRequest? request, ProbeStore store, WorkflowHostClient workflows, CancellationToken ct) =>
{
    if (!ProbeIds.IsValid(probeId))
    {
        return Results.Problem(statusCode: 400, title: "probe id must be 1-64 letters, digits or '-'");
    }

    // SQL start record first (unique): the probe exists durably before any workflow call.
    if (!await store.TryCreateAsync(probeId, ct))
    {
        return Results.Problem(statusCode: 409, title: "probe id already used", detail: "probe ids are unique; use a new id");
    }

    var input = new RuntimeProbeInput(probeId, request?.TimerSeconds ?? 5, request?.EffectDelayMilliseconds ?? 0);
    var started = await workflows.StartAsync(RuntimeProbe.WorkflowName, RuntimeProbe.InstanceId(probeId), input, ct: ct);
    return Results.Accepted($"/system/probes/runtime/{probeId}", new { probeId, started.InstanceId, started.Started, started.Status });
});

// Deliberately schedules the probe's instance id again, replacing the completed instance: this is
// how the probe proves that a replaced or restarted instance cannot repeat committed effects.
probes.MapPost("/{probeId}/reschedule", async (string probeId, ProbeStore store, WorkflowHostClient workflows, CancellationToken ct) =>
{
    if (await store.GetAsync(probeId, ct) is null)
    {
        return Results.NotFound();
    }

    var started = await workflows.StartAsync(RuntimeProbe.WorkflowName, RuntimeProbe.InstanceId(probeId),
        new RuntimeProbeInput(probeId, 1, 0), replaceTerminal: true, ct);
    return Results.Accepted($"/system/probes/runtime/{probeId}", started);
});

probes.MapGet("/{probeId}", async (string probeId, ProbeStore store, WorkflowHostClient workflows, CancellationToken ct) =>
{
    var record = await store.GetAsync(probeId, ct);
    return record is null
        ? Results.NotFound()
        : Results.Ok(new { probe = record, workflow = await workflows.GetStateAsync(RuntimeProbe.InstanceId(probeId), ct) });
});

await app.RunAsync();
return 0;

// Transaction checkpoints are failpoints in drill builds (no-ops otherwise).
static StepCheckpoint Checkpoints(IServiceProvider sp) =>
    Failpoints.Compiled ? sp.GetRequiredService<Failpoints>().HitAsync : StepCheckpoints.None;

internal static partial class ProbeIds
{
    public static bool IsValid(string probeId) => Pattern().IsMatch(probeId);

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex Pattern();
}
