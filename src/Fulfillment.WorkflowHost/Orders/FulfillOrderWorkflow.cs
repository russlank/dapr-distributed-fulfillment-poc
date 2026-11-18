using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>
/// Fulfillment workflow, minimal shell: the first step is the execution claim in OrderDb (on
/// order-api). A run for an order that is already terminal ends there without any effect; every
/// other run currently ends after the claim. The fulfillment steps follow the claim in the next
/// implementation step. Deterministic: no clock, random, or I/O here.
/// </summary>
public sealed partial class FulfillOrderWorkflow : Workflow<FulfillmentInput, FulfillmentResult>
{
    private static readonly WorkflowTaskOptions OnOrderApi = new(null, AppIds.OrderApi);

    public override async Task<FulfillmentResult> RunAsync(WorkflowContext context, FulfillmentInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var log = context.CreateReplaySafeLogger<FulfillOrderWorkflow>();

        var claim = await context.CallActivityAsync<ExecutionClaimResult>(
            FulfillmentWorkflow.ClaimExecutionActivity, new ExecutionClaimRequest(input.OrderId, context.InstanceId), OnOrderApi);
        if (!claim.Proceed)
        {
            LogFenced(log, context.InstanceId, claim.OrderStatus);
            return new FulfillmentResult(FulfillmentOutcomes.Fenced, claim.ExecutionClaims);
        }

        context.SetCustomStatus("claimed");
        return new FulfillmentResult(FulfillmentOutcomes.ShellClaimed, claim.ExecutionClaims);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "fulfillment {InstanceId} fenced: order already {Status}, no effects")]
    private static partial void LogFenced(ILogger logger, string instanceId, string status);
}
