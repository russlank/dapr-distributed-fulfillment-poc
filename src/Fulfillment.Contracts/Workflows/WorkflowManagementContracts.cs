using System.Text.Json;

namespace Fulfillment.Contracts.Workflows;

/// <summary>Routes of the workflow host's internal management API (Dapr service invocation only).</summary>
public static class WorkflowManagementRoutes
{
    public const string Prefix = "/internal/workflows";

    /// <summary>Instance-id prefix of diagnostic probes (the only instances that may be replaced).</summary>
    public const string ProbePrefix = "probe-";

    public static string Start(string workflowName, string instanceId, bool replaceTerminal = false) =>
        $"internal/workflows/{workflowName}/{instanceId}/start" + (replaceTerminal ? "?replaceTerminal=true" : string.Empty);

    public static string State(string instanceId) => $"internal/workflows/{instanceId}";

    public static string Event(string instanceId, string eventName) => $"internal/workflows/{instanceId}/events/{eventName}";
}

/// <param name="Started">False when an instance with this id already existed (in any state).</param>
public sealed record WorkflowStartResult(string InstanceId, bool Started, string Status);

public sealed record WorkflowEventResult(string InstanceId, bool Raised, string Status);

public sealed record WorkflowStateView(
    string InstanceId,
    bool Exists,
    string? Status,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUpdatedAt,
    JsonElement? Output,
    string? Failure,
    string? CustomStatus = null);
