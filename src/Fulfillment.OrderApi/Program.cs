using System.Text.RegularExpressions;
using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Messaging;
using Fulfillment.OrderApi;
using Fulfillment.OrderApi.Diagnostics;
using Fulfillment.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.OrderApi, UsesDaprSidecar: true, DatabaseConnectionName: "OrderDb"));

var orderDb = builder.Configuration.GetConnectionString("OrderDb")!;
builder.Services.AddSingleton(new ProbeStore(orderDb));
builder.Services.AddSidecarInvoker();
builder.Services.AddTransient<WorkflowHostClient>();

// The order API hosts the activities that write OrderDb; workflows themselves run on the workflow host.
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterActivity<ClaimProbeExecutionActivity>(ActivityNames.Claim);
    options.RegisterActivity<CompleteProbeExecutionActivity>(ActivityNames.Complete);
});

var app = builder.Build();
await app.Services.GetRequiredService<ProbeStore>().EnsureSchemaAsync();
app.MapServiceDefaults();

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

app.Run();

internal static partial class ProbeIds
{
    public static bool IsValid(string probeId) => Pattern().IsMatch(probeId);

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex Pattern();
}
