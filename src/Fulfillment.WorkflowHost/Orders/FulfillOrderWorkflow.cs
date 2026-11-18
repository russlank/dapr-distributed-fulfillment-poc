using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>
/// Fulfillment of one order (M1 scope): execution claim → validation → independent compute checks
/// in parallel → stored checks decide whether a review is required → durable review wait (if
/// required) → simulated fulfillment steps → terminal transaction that completes the order and
/// closes its start request.
///
/// All business state is in OrderDb and changed only by the order-api activities, each of which is
/// conditional (expected status) or keyed (operation key), so a retried activity or a repeated run
/// of the same instance id repeats no committed effect. Any step that finds the order terminal ends
/// the run without effects. Deterministic: no clock, random, or I/O here; inputs of later steps come
/// from earlier activity results (workflow history), keys are derived from the order id and step,
/// and timers are computed from the stored deadline and the workflow's own clock.
///
/// Review: SQL decides, the workflow only waits. Each round is read from SQL before the first wait
/// and after every wake-up (signal or timer, in whatever order Dapr delivers them); only the expiry
/// activity takes a review out of Pending by time, and only when SQL time reached the deadline.
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
        var recorded = await context.CallActivityAsync<ChecksRecorded>(
            FulfillmentWorkflow.RecordChecksActivity, new RecordChecksRequest(orderId, await priceTask, await riskTask), OnOrderApi);
        if (recorded.Terminal)
        {
            return Fenced(log, context, recorded.OrderStatus, claim);
        }

        // The stored (first committed) risk result decides, not this run's computation.
        if (recorded.ReviewRequired)
        {
            var review = await ReviewAsync(context, log, orderId);
            if (review is not null)
            {
                return review.OrderStatus == "Rejected" && review.Outcome == FulfillmentOutcomes.Rejected
                    ? review with { ExecutionClaims = claim.ExecutionClaims }
                    : Fenced(log, context, review.OrderStatus, claim);
            }
        }

        var begun = await context.CallActivityAsync<StepResult>(FulfillmentWorkflow.BeginFulfillmentActivity, new BeginFulfillmentRequest(orderId), OnOrderApi);
        if (begun.Terminal)
        {
            return Fenced(log, context, begun.OrderStatus, claim);
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

    /// <summary>
    /// The review: opens round 1, then resolves and waits until SQL says Approved (returns null: go
    /// on), Rejected or finally expired (returns Rejected), or the order is terminal for another
    /// reason (returns that status).
    /// </summary>
    private static async Task<FulfillmentResult?> ReviewAsync(WorkflowContext context, ILogger log, Guid orderId)
    {
        var round = await context.CallActivityAsync<ReviewRound>(FulfillmentWorkflow.CreateReviewTaskActivity, new CreateReviewRequest(orderId), OnOrderApi);
        if (round.Terminal)
        {
            return new FulfillmentResult(FulfillmentOutcomes.Fenced, round.OrderStatus, 0);
        }

        var wake = ReviewWakes.Initial;
        while (true)
        {
            var resolution = await context.CallActivityAsync<ReviewResolution>(FulfillmentWorkflow.ResolveReviewActivity,
                new ResolveReviewRequest(orderId, round.ReviewId, wake), OnOrderApi);
            LogResolved(log, context.InstanceId, round.Round, wake, resolution.State);
            if (resolution.State == ReviewStates.Approved)
            {
                return null;
            }

            if (resolution.State == ReviewStates.Rejected)
            {
                context.SetCustomStatus("review-rejected");
                return new FulfillmentResult(resolution.OrderTerminal ? FulfillmentOutcomes.Rejected : FulfillmentOutcomes.Fenced, resolution.OrderStatus, 0);
            }

            if (resolution.OrderTerminal)
            {
                return new FulfillmentResult(FulfillmentOutcomes.Fenced, resolution.OrderStatus, 0);
            }

            if (resolution.State == ReviewStates.Expired || resolution.DeadlinePassed)
            {
                var expiry = await context.CallActivityAsync<ExpiryResult>(FulfillmentWorkflow.ExpireOrEscalateReviewActivity,
                    new ExpireReviewRequest(orderId, round.ReviewId), OnOrderApi);
                switch (expiry.Outcome)
                {
                    case ExpiryOutcomes.NextRound:
                        round = expiry.NextRound!;
                        wake = ReviewWakes.Initial;
                        continue;
                    case ExpiryOutcomes.FinalExpiry:
                        context.SetCustomStatus("review-expired");
                        return new FulfillmentResult(FulfillmentOutcomes.Rejected, expiry.OrderStatus, 0);
                    case ExpiryOutcomes.AlreadyDecided:
                        wake = ReviewWakes.Recheck;
                        continue;
                    case ExpiryOutcomes.AlreadyTerminal:
                        return new FulfillmentResult(FulfillmentOutcomes.Fenced, expiry.OrderStatus, 0);
                }

                // NotDue: SQL time is still before the deadline; wait again.
            }

            // Wait for the decision's signal or for the deadline (plus grace), whichever comes first;
            // either way the next iteration reads SQL. A timer that fires early, or a wake-up after
            // the deadline, never decides anything by itself. The timer is never set in the past.
            context.SetCustomStatus($"awaiting-review:{round.Round}");
            var fireAt = round.DeadlineAtUtc + round.TimerGrace;
            var earliest = context.CurrentUtcDateTime + round.TimerGrace;
            using var cancel = new CancellationTokenSource();
            var timer = context.CreateTimer(fireAt > earliest ? fireAt : earliest, cancel.Token);
            var signal = context.WaitForExternalEventAsync<ReviewSignal>(ReviewSignals.EventName(round.ReviewId), cancel.Token);
            var first = await Task.WhenAny(timer, signal);
            wake = first == signal ? ReviewWakes.Signal : ReviewWakes.Timer;
            await cancel.CancelAsync();
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "fulfillment {InstanceId}: review round {Round} resolved after {Wake}: {State}")]
    private static partial void LogResolved(ILogger logger, string instanceId, int round, string wake, string state);
}
