using Dapr.Workflow;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>
/// Named patches of the fulfillment workflow, the versioning rule for changing a workflow while instances run: a
/// change to the path is guarded by <see cref="Applies"/>. A run that starts with the patch takes the new path and
/// records the patch in its history; an instance started before it replays its recorded path without the patch. A
/// patch can be held back per host (environment variable <c>WORKFLOW_PATCHES_DISABLED</c>, comma-separated names):
/// new runs then keep the old path until it is enabled, and the choice of each run stays as recorded, also across
/// retries and replays. Holding back a patch that running instances already recorded leaves them waiting until a host
/// with the patch enabled runs them (Dapr stalls such an instance instead of replaying it differently).
/// </summary>
public sealed class WorkflowPatches
{
    /// <summary>The run records which workflow version executes the order (<c>WorkflowStartRequest.ExecutionVersion</c>).</summary>
    public const string ExecutionVersion2 = "execution-version-2";

    public const string DisabledVariable = "WORKFLOW_PATCHES_DISABLED";

    private static readonly string[] Known = [ExecutionVersion2];

    private readonly HashSet<string> disabled;

    public WorkflowPatches(IEnumerable<string> disabled) => this.disabled = new HashSet<string>(disabled, StringComparer.Ordinal);

    /// <summary>The patch set of this host process (set once at startup).</summary>
    public static WorkflowPatches Current { get; set; } = new([]);

    public static WorkflowPatches FromEnvironment(string? value) =>
        new((value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public IReadOnlyList<string> Enabled => Known.Where(IsEnabled).ToList();

    public IReadOnlyList<string> Disabled => Known.Where(p => !IsEnabled(p)).ToList();

    public bool IsEnabled(string name) => !disabled.Contains(name);

    /// <summary>True when this run takes the patched path: the patch is enabled on this host and the run has it (new run, or recorded in its history).</summary>
    public static bool Applies(WorkflowContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Current.IsEnabled(name) && context.IsPatched(name);
    }
}
