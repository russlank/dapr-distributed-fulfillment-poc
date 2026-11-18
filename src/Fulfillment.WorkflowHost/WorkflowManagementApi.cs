using System.Text.Json;
using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost;

/// <summary>
/// Internal workflow-management API of the workflow host, called by other roles through Dapr service
/// invocation (sidecar-only route, app token required). A Dapr workflow instance belongs to the app
/// whose sidecar scheduled it, and DaprWorkflowClient cannot target another app (observed: an
/// instance scheduled through order-api failed with WorkflowNotFound). Roles that own the SQL start
/// and signal records therefore manage workflows here instead of through their own sidecar.
/// The API holds no business logic and no database.
/// </summary>
public static class WorkflowManagementApi
{
    public static void MapWorkflowManagement(this WebApplication app)
    {
        var api = app.MapGroup(WorkflowManagementRoutes.Prefix);

        // State-first start: an existing instance in any state is reported, never replaced.
        api.MapPost("/{workflowName}/{instanceId}/start", async (string workflowName, string instanceId, JsonElement input,
            bool? replaceTerminal, DaprWorkflowClient client, CancellationToken ct) =>
        {
            var state = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            var exists = state is { Exists: true };

            // Diagnostic only: replacing a terminal instance is allowed for probe instances, to prove that
            // a replaced instance cannot repeat committed effects. Never for business workflows.
            var replace = replaceTerminal == true && instanceId.StartsWith(WorkflowManagementRoutes.ProbePrefix, StringComparison.Ordinal);
            if (exists && !replace)
            {
                return Results.Ok(new WorkflowStartResult(instanceId, Started: false, state!.RuntimeStatus.ToString()));
            }

            await client.ScheduleNewWorkflowAsync(workflowName, instanceId, input);
            return Results.Ok(new WorkflowStartResult(instanceId, Started: true, "Scheduled"));
        });

        api.MapGet("/{instanceId}", async (string instanceId, DaprWorkflowClient client, CancellationToken ct) =>
        {
            var state = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: true, ct);
            if (state is not { Exists: true })
            {
                return Results.Ok(new WorkflowStateView(instanceId, false, null, null, null, null, null));
            }

            JsonElement? output = state.IsWorkflowCompleted ? state.ReadOutputAs<JsonElement>() : null;
            return Results.Ok(new WorkflowStateView(
                instanceId,
                true,
                state.RuntimeStatus.ToString(),
                state.CreatedAt,
                state.LastUpdatedAt,
                output,
                state.FailureDetails is null ? null : $"{state.FailureDetails.ErrorType}: {state.FailureDetails.ErrorMessage}"));
        });

        api.MapPost("/{instanceId}/events/{eventName}", async (string instanceId, string eventName, JsonElement payload,
            DaprWorkflowClient client, CancellationToken ct) =>
        {
            // Raising to a completed instance is a silent no-op and to a missing one an error (observed),
            // so callers check the state first; the state is returned for that purpose.
            var state = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            if (state is not { Exists: true, IsWorkflowRunning: true })
            {
                return Results.Ok(new WorkflowEventResult(instanceId, Raised: false, state is { Exists: true } ? state.RuntimeStatus.ToString() : "NotFound"));
            }

            await client.RaiseEventAsync(instanceId, eventName, payload, ct);
            return Results.Ok(new WorkflowEventResult(instanceId, Raised: true, state.RuntimeStatus.ToString()));
        });
    }
}
