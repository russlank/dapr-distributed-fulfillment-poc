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

    /// <summary>Drill: terminates (if needed) and purges fulfill-{orderId:N}, so the instance no longer exists (a lost instance).</summary>
    public const string PurgePath = "/internal/drill/fulfillment/{orderId:guid}/purge";

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
        app.MapPost(PurgePath, PurgeAsync);
    }

    private static async Task<IResult> PurgeAsync(Guid orderId, DaprWorkflowClient client, ILoggerFactory loggers, CancellationToken ct)
    {
        var instanceId = FulfillmentWorkflow.InstanceId(orderId);
        var before = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
        var previous = before is { Exists: true } ? before.RuntimeStatus.ToString() : "NotFound";
        LogTerminate(loggers.CreateLogger(typeof(DrillControls)), instanceId, previous + " (purge)");
        try
        {
            if (before is { Exists: true, IsWorkflowCompleted: false })
            {
                await TerminateBoundedAsync(client, instanceId, "drill: purged", ct);
            }

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(TimeSpan.FromSeconds(15));
            var purged = await client.PurgeInstanceAsync(instanceId, bound.Token);
            var after = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            return Results.Ok(new { instanceId, purged, previousStatus = previous, exists = after is { Exists: true }, error = (string?)null });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Results.Ok(new { instanceId, purged = false, previousStatus = previous, exists = (bool?)null, error = $"{e.GetType().Name}: {e.Message}" });
        }
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
            var answered = await TerminateBoundedAsync(client, instanceId, "drill: terminated", ct);
            var after = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            return Results.Ok(new
            {
                instanceId, terminated = after is { RuntimeStatus: WorkflowRuntimeStatus.Terminated }, previousStatus = previous,
                status = after?.RuntimeStatus.ToString(), terminateAnswered = answered, error = (string?)null,
            });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Results.Ok(new { instanceId, terminated = false, previousStatus = previous, status = (string?)null, error = $"{e.GetType().Name}: {e.Message}" });
        }
    }

    /// <summary>
    /// Terminates and waits for the terminated state, at most 15 s. On Dapr 1.19.1 terminating (or purging) an instance
    /// that recorded a workflow patch never answers, and the instance stays Stalled for good (also across restarts), so
    /// the wait is bounded and the caller reads the recorded state. Returns whether the runtime answered in time.
    /// </summary>
    private static async Task<bool> TerminateBoundedAsync(DaprWorkflowClient client, string instanceId, string reason, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.TerminateWorkflowAsync(instanceId, reason, bound.Token);
            await client.WaitForWorkflowCompletionAsync(instanceId, getInputsAndOutputs: false, bound.Token);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is OperationCanceledException || e is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Cancelled or Grpc.Core.StatusCode.DeadlineExceeded }))
        {
            // The bound passed: the instance's state is read by the caller (Stalled for a patched instance).
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "drill control: terminating {InstanceId} (runtime status {Previous})")]
    private static partial void LogTerminate(ILogger logger, string instanceId, string previous);

    [LoggerMessage(Level = LogLevel.Warning, Message = "drill control: scheduling {InstanceId} directly (previous runtime status {Previous})")]
    private static partial void LogSchedule(ILogger logger, string instanceId, string previous);
}
#endif
