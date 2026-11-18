using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost;

public enum ManagementDecision
{
    Allowed,
    CallerNotAuthorized,
    WorkflowTypeNotSupported,
    ReplacementNotAllowed,
}

/// <summary>
/// Who may manage which workflows through the internal management API. The sidecar-only route and
/// the app token already keep out everything that does not come through a Dapr sidecar; this policy
/// additionally limits the calling app (Dapr sets dapr-caller-app-id on service invocation) and the
/// workflow types. Replacing an existing instance is a diagnostic capability, limited to probe
/// instances of the probe workflow. The state-first existence check of a start is not atomic under
/// concurrent requests; business correctness relies on the SQL start record, the execution claim,
/// and keyed effects, never on this API.
/// </summary>
public sealed class WorkflowManagementPolicy
{
    public const string CallerHeader = "dapr-caller-app-id";

    private readonly HashSet<string> callers;
    private readonly HashSet<string> workflowTypes;

    public WorkflowManagementPolicy(IEnumerable<string> callers, IEnumerable<string> workflowTypes)
    {
        this.callers = new HashSet<string>(callers, StringComparer.Ordinal);
        this.workflowTypes = new HashSet<string>(workflowTypes, StringComparer.Ordinal);
    }

    /// <summary>M0 policy: the order API manages the runtime probe. M1 adds the fulfillment workflow.</summary>
    public static WorkflowManagementPolicy Default { get; } = new([AppIds.OrderApi], [RuntimeProbe.WorkflowName]);

    public ManagementDecision CanStart(string? caller, string workflowName, string instanceId, bool replaceTerminal)
    {
        if (!IsCaller(caller))
        {
            return ManagementDecision.CallerNotAuthorized;
        }

        if (!workflowTypes.Contains(workflowName))
        {
            return ManagementDecision.WorkflowTypeNotSupported;
        }

        if (replaceTerminal && !(workflowName == RuntimeProbe.WorkflowName
                                 && instanceId.StartsWith(WorkflowManagementRoutes.ProbePrefix, StringComparison.Ordinal)))
        {
            return ManagementDecision.ReplacementNotAllowed;
        }

        return ManagementDecision.Allowed;
    }

    /// <summary>Queries and events: the caller must be authorized; an existing instance must be of a supported type.</summary>
    public ManagementDecision CanAccess(string? caller, string? existingWorkflowName)
    {
        if (!IsCaller(caller))
        {
            return ManagementDecision.CallerNotAuthorized;
        }

        return existingWorkflowName is not null && !workflowTypes.Contains(existingWorkflowName)
            ? ManagementDecision.WorkflowTypeNotSupported
            : ManagementDecision.Allowed;
    }

    private bool IsCaller(string? caller) => caller is not null && callers.Contains(caller);
}
