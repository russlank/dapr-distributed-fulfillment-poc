using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>
/// Fulfillment of one order (M1 scope): execution claim → validation → independent compute checks
/// in parallel → review gate (no-review path only) → simulated fulfillment steps → terminal
/// transaction that completes the order and closes its start request.
///
/// All business state is in OrderDb and changed only by the order-api activities, each of which is
/// conditional (expected status) or keyed (operation key), so a retried activity or a repeated run
/// of the same instance id repeats no committed effect. Any step that finds the order terminal ends
/// the run without effects. Deterministic: no clock, random, or I/O here; inputs of later steps come
/// from earlier activity results (workflow history), keys are derived from the order id and step.
///
/// The simulated steps stand in for reservation, payment, and shipment, which do not exist yet:
/// they call no other service and record "stub-" effects only.
/// </summary>
public sealed partial class FulfillOrderWorkflow : Workflow<FulfillmentInput, FulfillmentResult>
{
    /// <summary>Retries for failed activities (a transient SQL error, a replica stopping mid-call); every step is safe to repeat, the claim included (extra claims are counted, never effects).</summary>
    private static readonly WorkflowRetryPolicy Retry = new(5, TimeSpan.FromSeconds(1), 2, TimeSpan.FromSeconds(30));

    private static readonly WorkflowTaskOptions OnOrderApi = new(Retry, AppIds.OrderApi);
    private static readonly WorkflowTaskOptions OnCompute = new(Retry, AppIds.ComputeWorker);

    public override async Task<FulfillmentResult> RunAsync(WorkflowContext context, FulfillmentInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var log = context.CreateReplaySafeLogger<FulfillOrderWorkflow>();
        var orderId = input.OrderId;

        var claim = await context.CallActivityAsync<ExecutionClaimResult>(
            FulfillmentWorkflow.ClaimExecutionActivity, new ExecutionClaimRequest(orderId, context.InstanceId), OnOrderApi);
        if (!claim.Proceed)
        {
            LogFenced(log, context.InstanceId, claim.OrderStatus);
            return new FulfillmentResult(FulfillmentOutcomes.Fenced, claim.OrderStatus, claim.ExecutionClaims);
        }

        context.SetCustomStatus("validating");
        var validation = await context.CallActivityAsync<OrderValidationResult>(
            FulfillmentWorkflow.ValidateOrderActivity, new ValidateOrderRequest(orderId, context.InstanceId), OnOrderApi);
        if (validation.Outcome == StepOutcomes.AlreadyTerminal)
        {
            return Fenced(log, context, validation.OrderStatus, claim);
        }

        if (validation.Outcome == StepOutcomes.Rejected)
        {
            LogRejected(log, context.InstanceId, validation.RuleCode);
            context.SetCustomStatus("rejected");
            return new FulfillmentResult(FulfillmentOutcomes.Rejected, validation.OrderStatus, claim.ExecutionClaims);
        }

        // Independent checks: both are scheduled before either is awaited, so they run concurrently.
        context.SetCustomStatus("checks");
        var checkInput = new OrderCheckInput(orderId, validation.CustomerId, validation.Lines);
        var priceTask = context.CallActivityAsync<PriceQuote>(FulfillmentWorkflow.PriceOrderActivity, checkInput, OnCompute);
        var riskTask = context.CallActivityAsync<RiskAssessment>(FulfillmentWorkflow.AssessOrderRiskActivity, checkInput, OnCompute);
        await Task.WhenAll(priceTask, riskTask);
        var price = await priceTask;
        var risk = await riskTask;
        var recorded = await context.CallActivityAsync<StepResult>(
            FulfillmentWorkflow.RecordChecksActivity, new RecordChecksRequest(orderId, price, risk), OnOrderApi);
        if (recorded.Terminal)
        {
            return Fenced(log, context, recorded.OrderStatus, claim);
        }

        // Review gate. Only the path without review exists in this version: the durable review wait
        // (review task, decision, signal) is not implemented, and an approval is never assumed. A
        // required review therefore fails the run visibly instead of continuing without a decision.
        if (risk.ReviewRequired)
        {
            throw new NotSupportedException($"order {orderId:N} requires a review (risk score {risk.Score}); the review wait is not available in this version");
        }

        context.SetCustomStatus("fulfilling");
        foreach (var line in validation.Lines.OrderBy(l => l.LineNumber))
        {
            var step = await context.CallActivityAsync<StepResult>(FulfillmentWorkflow.ApplyStubStepActivity,
                new StubStepRequest(orderId, StubSteps.Fulfil, StubSteps.LineQualifier(line.LineNumber)), OnOrderApi);
            if (step.Terminal)
            {
                return Fenced(log, context, step.OrderStatus, claim);
            }
        }

        var completed = await context.CallActivityAsync<StepResult>(FulfillmentWorkflow.CompleteStubFulfillmentActivity,
            new CompleteFulfillmentRequest(orderId, context.InstanceId), OnOrderApi);
        if (completed.Terminal)
        {
            return Fenced(log, context, completed.OrderStatus, claim);
        }

        context.SetCustomStatus("completed");
        return new FulfillmentResult(FulfillmentOutcomes.Completed, completed.OrderStatus, claim.ExecutionClaims);
    }

    private static FulfillmentResult Fenced(ILogger log, WorkflowContext context, string status, ExecutionClaimResult claim)
    {
        LogFenced(log, context.InstanceId, status);
        context.SetCustomStatus("fenced");
        return new FulfillmentResult(FulfillmentOutcomes.Fenced, status, claim.ExecutionClaims);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "fulfillment {InstanceId} fenced: order already {Status}, no further effects")]
    private static partial void LogFenced(ILogger logger, string instanceId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "fulfillment {InstanceId}: order rejected by validation ({RuleCode})")]
    private static partial void LogRejected(ILogger logger, string instanceId, string? ruleCode);
}
