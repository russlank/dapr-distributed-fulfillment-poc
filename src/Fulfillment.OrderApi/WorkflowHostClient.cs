using System.Text.Json;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Messaging;

namespace Fulfillment.OrderApi;

/// <summary>
/// Manages workflow instances owned by the workflow host, through its internal API (Dapr service
/// invocation with caller-side bounded retries). The order API cannot use its own DaprWorkflowClient
/// for this: an instance scheduled through its sidecar would belong to the order-api app.
/// </summary>
public sealed class WorkflowHostClient(SidecarInvoker invoker)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WorkflowStartResult> StartAsync(string workflowName, string instanceId, object input, bool replaceTerminal = false, CancellationToken ct = default)
    {
        var result = await invoker.PostAsync(AppIds.WorkflowHost, WorkflowManagementRoutes.Start(workflowName, instanceId, replaceTerminal),
            input, idempotencyKey: $"start:{instanceId}", ct);
        return Parse<WorkflowStartResult>(result, "start");
    }

    public async Task<WorkflowStateView> GetStateAsync(string instanceId, CancellationToken ct = default) =>
        Parse<WorkflowStateView>(await invoker.GetAsync(AppIds.WorkflowHost, WorkflowManagementRoutes.State(instanceId), ct), "state");

    private static T Parse<T>(InvocationResult result, string operation)
    {
        if (!result.IsSuccess)
        {
            throw new WorkflowHostUnavailableException(
                $"workflow host {operation} failed after {result.Attempts} attempt(s): {(int?)result.Status} {result.TransportError}");
        }

        return JsonSerializer.Deserialize<T>(result.Body, Json)
            ?? throw new WorkflowHostUnavailableException($"workflow host {operation} returned an empty body");
    }
}

public sealed class WorkflowHostUnavailableException(string message) : Exception(message);
