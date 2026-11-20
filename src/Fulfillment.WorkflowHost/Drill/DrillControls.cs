#if FULFILLMENT_FAILPOINTS
using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;

namespace Fulfillment.WorkflowHost.Drill;

/// <summary>
/// Drill-only controls of the workflow host. They exist only in drill builds (compiled out of
/// release images) and are mapped only in the drill environment, under the sidecar-only prefix
/// (app token required). They deliberately bypass the management API's rules to create the
/// situations the execution fence must withstand.
/// </summary>
public static partial class DrillControls
{
    public const string SchedulePath = "/internal/drill/fulfillment/{orderId:guid}/schedule";

    public const string TerminatePath = "/internal/drill/fulfillment/{orderId:guid}/terminate";

    public static void MapDrillControls(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!Failpoints.Active)
        {
            return;
        }

        // Schedules fulfill-{orderId:N} directly through the Dapr workflow API, without the existence
        // check of the management API: simulates a manual restart or id reuse for an order whatever
        // its state. Dapr's answer (accepted, or the error) is returned as observed.
        app.MapPost(SchedulePath, async (Guid orderId, DaprWorkflowClient client, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var instanceId = FulfillmentWorkflow.InstanceId(orderId);
            var before = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            var previous = before is { Exists: true } ? before.RuntimeStatus.ToString() : "NotFound";
            LogSchedule(loggers.CreateLogger(typeof(DrillControls)), instanceId, previous);
            try
            {
                await client.ScheduleNewWorkflowAsync(FulfillmentWorkflow.Name, instanceId, new FulfillmentInput(orderId));
                return Results.Ok(new { instanceId, scheduled = true, previousStatus = previous, error = (string?)null });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.Ok(new { instanceId, scheduled = false, previousStatus = previous, error = $"{e.GetType().Name}: {e.Message}" });
            }
        });
        app.MapPost(TerminatePath, TerminateAsync);
    }

    /// <summary>
    /// Terminates fulfill-{orderId:N} (a workflow that ended without finishing its order: the situation a repair
    /// workflow exists for). Returns the runtime status before and after.
    /// </summary>
    private static async Task<IResult> TerminateAsync(Guid orderId, DaprWorkflowClient client, ILoggerFactory loggers, CancellationToken ct)
    {
        var instanceId = FulfillmentWorkflow.InstanceId(orderId);
        var before = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
        var previous = before is { Exists: true } ? before.RuntimeStatus.ToString() : "NotFound";
        LogTerminate(loggers.CreateLogger(typeof(DrillControls)), instanceId, previous);
        try
        {
            await client.TerminateWorkflowAsync(instanceId, "drill: terminated", ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            var after = await client.WaitForWorkflowCompletionAsync(instanceId, getInputsAndOutputs: false, wait.Token);
            return Results.Ok(new { instanceId, terminated = true, previousStatus = previous, status = after.RuntimeStatus.ToString(), error = (string?)null });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Results.Ok(new { instanceId, terminated = false, previousStatus = previous, status = (string?)null, error = $"{e.GetType().Name}: {e.Message}" });
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "drill control: terminating {InstanceId} (runtime status {Previous})")]
    private static partial void LogTerminate(ILogger logger, string instanceId, string previous);

    [LoggerMessage(Level = LogLevel.Warning, Message = "drill control: scheduling {InstanceId} directly (previous runtime status {Previous})")]
    private static partial void LogSchedule(ILogger logger, string instanceId, string previous);
}
#endif
