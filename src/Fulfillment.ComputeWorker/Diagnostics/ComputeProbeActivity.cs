using Dapr.Workflow;
using Fulfillment.ServiceDefaults;

namespace Fulfillment.ComputeWorker.Diagnostics;

/// <summary>Stateless probe step: reports which compute replica ran it. No side effects.</summary>
public sealed class ComputeProbeActivity(RuntimeIdentity identity) : WorkflowActivity<string, string>
{
    public override Task<string> RunAsync(WorkflowActivityContext context, string input) => Task.FromResult(identity.Replica);
}
