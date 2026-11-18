using System.Text.Json;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Messaging;

namespace Fulfillment.OrderApi.Dispatch;

public enum StartOutcomeKind
{
    /// <summary>The workflow host scheduled a new instance.</summary>
    Started,

    /// <summary>An instance with the id exists already (any runtime status); nothing was scheduled.</summary>
    AlreadyExists,

    /// <summary>No usable answer (transport errors or 5xx after the bounded retries): the instance may or may not exist.</summary>
    Ambiguous,

    /// <summary>The workflow host refused the request (4xx, e.g. policy): retrying soon will not help.</summary>
    Rejected,
}

public sealed record StartOutcome(StartOutcomeKind Kind, string? ReportedStatus);

/// <summary>Starts fulfillment instances; the boundary the dispatcher's tests replace.</summary>
public interface IWorkflowStarter
{
    Task<StartOutcome> StartAsync(string instanceId, FulfillmentInput input, CancellationToken ct);
}

/// <summary>
/// Starts through the workflow host's management API (service invocation, caller-side bounded
/// retries with one idempotency key), never through the order API's own sidecar: an instance
/// scheduled there would belong to the order-api app. An ambiguous answer is resolved by the next
/// attempt, whose state-first start reports an instance that the lost answer did create.
/// </summary>
public sealed class WorkflowHostStarter(SidecarInvoker invoker) : IWorkflowStarter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StartOutcome> StartAsync(string instanceId, FulfillmentInput input, CancellationToken ct)
    {
        var result = await invoker.PostAsync(AppIds.WorkflowHost, WorkflowManagementRoutes.Start(FulfillmentWorkflow.Name, instanceId, replaceTerminal: false),
            input, idempotencyKey: $"start:{instanceId}", ct);
        if (result.IsSuccess)
        {
            var start = JsonSerializer.Deserialize<WorkflowStartResult>(result.Body, Json);
            return start is null
                ? new StartOutcome(StartOutcomeKind.Ambiguous, null)
                : new StartOutcome(start.Started ? StartOutcomeKind.Started : StartOutcomeKind.AlreadyExists, start.Status);
        }

        return result.Status is { } status && (int)status is >= 400 and < 500
            ? new StartOutcome(StartOutcomeKind.Rejected, ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new StartOutcome(StartOutcomeKind.Ambiguous, result.Status is null ? null : ((int)result.Status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
