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
/// Access is limited by <see cref="WorkflowManagementPolicy"/>. The API holds no business logic and
/// no database; its state-first start check is not atomic, so correctness stays with the caller's
/// SQL records, the execution claim, and keyed effects.
/// </summary>
public static class WorkflowManagementApi
{
    public static void MapWorkflowManagement(this WebApplication app, WorkflowManagementPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var api = app.MapGroup(WorkflowManagementRoutes.Prefix);

        api.MapPost("/{workflowName}/{instanceId}/start", async (string workflowName, string instanceId, JsonElement input,
            bool? replaceTerminal, HttpRequest request, DaprWorkflowClient client, CancellationToken ct) =>
        {
            var replace = replaceTerminal == true;
            var decision = policy.CanStart(Caller(request), workflowName, instanceId, replace);
            if (decision != ManagementDecision.Allowed)
            {
                return Denied(decision);
            }

            // State first: an existing instance in any state is reported, never replaced, unless the
            // policy allowed a diagnostic replacement (probe instances only).
            var state = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            if (state is { Exists: true } && !replace)
            {
                return Results.Ok(new WorkflowStartResult(instanceId, Started: false, state.RuntimeStatus.ToString()));
            }

            await client.ScheduleNewWorkflowAsync(workflowName, instanceId, input);
            return Results.Ok(new WorkflowStartResult(instanceId, Started: true, "Scheduled"));
        });

        api.MapGet("/{instanceId}", async (string instanceId, HttpRequest request, DaprWorkflowClient client, CancellationToken ct) =>
        {
            var state = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: true, ct);
            var decision = policy.CanAccess(Caller(request), state is { Exists: true } ? state.WorkflowName : null);
            if (decision != ManagementDecision.Allowed)
            {
                return Denied(decision);
            }

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
            HttpRequest request, DaprWorkflowClient client, CancellationToken ct) =>
        {
            // Raising to a completed instance is a silent no-op and to a missing one an error (observed),
            // so the state is checked first and returned; callers keep their signal row until it is used.
            var state = await client.GetWorkflowStateAsync(instanceId, getInputsAndOutputs: false, ct);
            var decision = policy.CanAccess(Caller(request), state is { Exists: true } ? state.WorkflowName : null);
            if (decision != ManagementDecision.Allowed)
            {
                return Denied(decision);
            }

            if (state is not { Exists: true, IsWorkflowRunning: true })
            {
                return Results.Ok(new WorkflowEventResult(instanceId, Raised: false, state is { Exists: true } ? state.RuntimeStatus.ToString() : "NotFound"));
            }

            await client.RaiseEventAsync(instanceId, eventName, payload, ct);
            return Results.Ok(new WorkflowEventResult(instanceId, Raised: true, state.RuntimeStatus.ToString()));
        });
    }

    private static string? Caller(HttpRequest request) =>
        request.Headers.TryGetValue(WorkflowManagementPolicy.CallerHeader, out var value) ? value.ToString() : null;

    private static IResult Denied(ManagementDecision decision) => decision switch
    {
        ManagementDecision.CallerNotAuthorized => Results.Problem(statusCode: 403, title: "caller not authorized for workflow management"),
        ManagementDecision.WorkflowTypeNotSupported => Results.Problem(statusCode: 400, title: "workflow type not supported by the management API"),
        _ => Results.Problem(statusCode: 403, title: "replacing an existing instance is not allowed for this workflow"),
    };
}
