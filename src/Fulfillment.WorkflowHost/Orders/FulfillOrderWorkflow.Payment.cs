using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.WorkflowHost.Orders;

/// <summary>Timing of the fulfillment after reservation (fixed per process; drill environment: short).</summary>
public sealed class FulfillmentTimings
{
    /// <summary>How long the holds are extended right before the capture (bounded by the authorization's expiry).</summary>
    public TimeSpan HoldExtension { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Validity the holds must still have for the capture to start: covers the capture's sends, status queries, and the consumption.</summary>
    public TimeSpan MinimumRemainingForCapture { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Rounds of a failing compensation (each one the payment service's full budget) before a CompensationExhausted case.</summary>
    public int CompensationRounds { get; init; } = 3;

    public TimeSpan CompensationBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A waiting case is re-read from SQL at least this often, also without a signal.</summary>
    public TimeSpan CaseRecheck { get; init; } = TimeSpan.FromMinutes(10);

    public static FulfillmentTimings ForEnvironment(string? environment) => environment == "drill"
        ? new FulfillmentTimings
        {
            HoldExtension = TimeSpan.FromSeconds(30),
            MinimumRemainingForCapture = TimeSpan.FromSeconds(15),
            CompensationBackoff = TimeSpan.FromSeconds(2),
            CaseRecheck = TimeSpan.FromSeconds(30),
        }
        : new FulfillmentTimings();

    /// <summary>Set once at host start, before any workflow runs.</summary>
    public static FulfillmentTimings Current { get; set; } = new();
}

public sealed partial class FulfillOrderWorkflow
{
    private static readonly WorkflowTaskOptions OnPayment = new(Retry, AppIds.PaymentService);

    /// <summary>What the order holds at the provider when it has to be compensated.</summary>
    private enum Money
    {
        None,
        Authorization,
        Capture,
    }

    /// <summary>
    /// After every line is reserved: authorize the amount, pack (simulated steps), pass the point of no return,
    /// extend the holds once (capped by the authorization), capture, consume the holds, complete. Every outcome other than success has a
    /// defined end: a decline rejects; a provider that cannot have been reached compensates (voiding the
    /// authorization's key, which also prohibits it); an undeterminable outcome opens an intervention case and waits
    /// for its resolution; a hold lost before the capture voids, after the capture refunds.
    /// </summary>
    private static async Task<FulfillmentResult> AfterReservationAsync(WorkflowContext context, ILogger log, Guid orderId, ExecutionClaimResult claim,
        OrderValidationResult validation, PriceQuote price)
    {
        var timings = FulfillmentTimings.Current;
        var lines = validation.Lines.Count;

        // Authorize.
        context.SetCustomStatus("authorizing");
        var auth = await PayAsync(context, orderId, PaymentSteps.Authorize, price, null);
        if (auth.Outcome == PaymentOutcomes.Ambiguous)
        {
            auth = await ResolveAmbiguousAsync(context, orderId, PaymentSteps.Authorize, price, auth);
        }

        switch (auth.Outcome)
        {
            case PaymentOutcomes.Authorized:
                break;
            case PaymentOutcomes.Declined:
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Rejected", ReasonCodes.PaymentDeclined, Money.None, price);
            case PaymentOutcomes.Unavailable:
                // Nothing can have committed; voiding the key still prohibits a late authorization.
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.PaymentProviderUnavailable, Money.Authorization, price);
            default:
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.PaymentFailed, Money.Authorization, price);
        }

        if (await AdvanceAsync(context, orderId, "PaymentAuthorized") is { } stopped1)
        {
            return Fenced(log, context, stopped1, claim);
        }

        // Pack (simulated steps, one per line).
        context.SetCustomStatus("packing");
        if (await AdvanceAsync(context, orderId, "Packing") is { } stopped2)
        {
            return Fenced(log, context, stopped2, claim);
        }

        foreach (var line in validation.Lines.OrderBy(l => l.LineNumber))
        {
            var step = await context.CallActivityAsync<StepResult>(FulfillmentWorkflow.ApplyStubStepActivity,
                new StubStepRequest(orderId, StubSteps.Fulfil, StubSteps.LineQualifier(line.LineNumber)), OnOrderApi);
            if (step.Terminal)
            {
                return Fenced(log, context, step.OrderStatus, claim);
            }
        }

        // The cancellation point of no return.
        var dispatch = await context.CallActivityAsync<BeginDispatchResult>(OrderLifecycleActivities.BeginDispatch, new BeginDispatchRequest(orderId), OnOrderApi);
        switch (dispatch.Outcome)
        {
            case DispatchOutcomes.AlreadyTerminal:
                return Fenced(log, context, dispatch.OrderStatus, claim);
            case DispatchOutcomes.CancelRequested:
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Cancelled", ReasonCodes.CancellationRequested, Money.Authorization, price);
        }

        // Extend the holds once, never beyond the authorization; capture only with enough validity left.
        context.SetCustomStatus("extending");
        ExtendHoldsResult extended;
        try
        {
            extended = await context.CallActivityAsync<ExtendHoldsResult>(InventoryActivities.ExtendOrderHolds,
                new ExtendHoldsRequest(orderId, lines, timings.HoldExtension, auth.AuthorizationExpiresAtUtc, timings.MinimumRemainingForCapture), OnInventory);
        }
        catch (WorkflowTaskFailedException)
        {
            return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.ReservationFailed, Money.Authorization, price);
        }

        if (extended.Outcome != InventoryOutcomes.Extended || extended.RemainingMilliseconds < timings.MinimumRemainingForCapture.TotalMilliseconds)
        {
            return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.ReservationExpired, Money.Authorization, price);
        }

        // Capture.
        context.SetCustomStatus("capturing");
        var capture = await PayAsync(context, orderId, PaymentSteps.Capture, price, PaymentSteps.Authorize);
        if (capture.Outcome == PaymentOutcomes.Ambiguous)
        {
            capture = await ResolveAmbiguousAsync(context, orderId, PaymentSteps.Capture, price, capture);
        }

        switch (capture.Outcome)
        {
            case PaymentOutcomes.Captured:
                break;
            case PaymentOutcomes.Unavailable:
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.PaymentProviderUnavailable, Money.Authorization, price);
            case PaymentOutcomes.Rejected:
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.PaymentCaptureFailed, Money.Authorization, price);
            default:
                return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.PaymentFailed, Money.Authorization, price);
        }

        // Consume: the holds must still be valid; a lost hold after the capture is refunded.
        ConsumeHoldsResult consumed;
        try
        {
            consumed = await context.CallActivityAsync<ConsumeHoldsResult>(InventoryActivities.ConsumeOrderHolds,
                new ConsumeHoldsRequest(orderId, lines), OnInventory);
        }
        catch (WorkflowTaskFailedException)
        {
            return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.ReservationFailed, Money.Capture, price);
        }

        if (consumed.Outcome != InventoryOutcomes.Consumed)
        {
            return await EndUnfulfilledAsync(context, log, orderId, claim, "Compensated", ReasonCodes.ReservationExpired, Money.Capture, price);
        }

        return await ShipAndDeliverAsync(context, log, orderId, claim, lines);
    }

    private static Task<PaymentOperationResult> PayAsync(WorkflowContext context, Guid orderId, string step, PriceQuote price, string? referenceStep,
        bool retryUnresolved = false, string qualifier = PaymentSteps.First) =>
        context.CallActivityAsync<PaymentOperationResult>(PaymentActivities.ExecutePayment,
            new PaymentOperationRequest(orderId, step, qualifier, price.TotalMinor, price.Currency, referenceStep, referenceStep is null ? null : PaymentSteps.First, retryUnresolved),
            OnPayment);

    /// <summary>One forward status step; returns the order's status when it is terminal (the run stops), otherwise null.</summary>
    private static async Task<string?> AdvanceAsync(WorkflowContext context, Guid orderId, string to)
    {
        var step = await context.CallActivityAsync<StepResult>(OrderLifecycleActivities.AdvanceFulfillment, new AdvanceFulfillmentRequest(orderId, to), OnOrderApi);
        return step.Terminal ? step.OrderStatus : null;
    }

    /// <summary>
    /// An undeterminable payment outcome: an AmbiguousPayment case, and the workflow waits for its resolution. The
    /// resolution's claim is never trusted by itself: the provider's record is read again (reconcile) and wins. If it
    /// is still undeterminable, another case is opened. ConfirmExternalFailure leaves the operation as it is, and the
    /// caller compensates by key (a void or refund by key also prohibits a late commit).
    /// </summary>
    private static async Task<PaymentOperationResult> ResolveAmbiguousAsync(WorkflowContext context, Guid orderId, string step, PriceQuote price, PaymentOperationResult ambiguous)
    {
        var result = ambiguous;
        for (var round = 1; result.Outcome == PaymentOutcomes.Ambiguous; round++)
        {
            var resolved = await WaitForCaseAsync(context, orderId, InterventionKinds.AmbiguousPayment, $"{step}-{round}",
                $"operation {result.OperationKey}: {result.Attempts} send(s), {result.StatusQueries} status query(ies), code {result.Code}");
            if (resolved.Action != CaseActions.ConfirmExternalSuccess)
            {
                return result;
            }

            result = await context.CallActivityAsync<PaymentOperationResult>(PaymentActivities.ReconcilePayment,
                new PaymentOperationRequest(orderId, step, PaymentSteps.First, price.TotalMinor, price.Currency), OnPayment);
        }

        return result;
    }

    /// <summary>
    /// Opens a case and waits until SQL says it is resolved: woken by the resolution's signal, or by a periodic
    /// re-read (so a lost signal only delays). The order sits in ManualInterventionRequired meanwhile; the
    /// resolution's transaction puts it back.
    /// </summary>
    private static async Task<CaseResult> WaitForCaseAsync(WorkflowContext context, Guid orderId, string kind, string qualifier, string evidence)
    {
        var current = await context.CallActivityAsync<CaseResult>(OrderLifecycleActivities.OpenInterventionCase,
            new OpenCaseRequest(orderId, kind, qualifier, evidence), OnOrderApi);
        while (current.State is CaseStates.Open or CaseStates.Reconciling)
        {
            context.SetCustomStatus($"intervention:{kind}");
            using var cancel = new CancellationTokenSource();
            var signal = context.WaitForExternalEventAsync<object>(InterventionSignals.EventName(current.CaseId), cancel.Token);
            var timer = context.CreateTimer(context.CurrentUtcDateTime + FulfillmentTimings.Current.CaseRecheck, cancel.Token);
            await Task.WhenAny(signal, timer);
#pragma warning disable CA1849 // workflow code awaits only workflow tasks (see the review wait)
            cancel.Cancel();
#pragma warning restore CA1849
            current = await context.CallActivityAsync<CaseResult>(OrderLifecycleActivities.ReadInterventionCase, new ReadCaseRequest(current.CaseId), OnOrderApi);
        }

        return current;
    }

    /// <summary>
    /// Compensates what the order holds at the provider: a void by the authorization's key, or a refund by the
    /// capture's key (a void that finds the authorization captured is followed by a refund). Unresolved outcomes are
    /// retried in rounds (same key); after the last round a CompensationExhausted case waits for an operator.
    /// </summary>
    private static async Task CompensatePaymentAsync(WorkflowContext context, Guid orderId, Money money, PriceQuote price)
    {
        var timings = FulfillmentTimings.Current;
        var step = money == Money.Capture ? PaymentSteps.Refund : PaymentSteps.Void;
        var reference = money == Money.Capture ? PaymentSteps.Capture : PaymentSteps.Authorize;
        for (var attempt = 1; ; attempt++)
        {
            var result = await PayAsync(context, orderId, step, price, reference, retryUnresolved: true);
            if (result.Outcome == PaymentOutcomes.Rejected && step == PaymentSteps.Void && result.Code == "AlreadyCaptured")
            {
                (step, reference) = (PaymentSteps.Refund, PaymentSteps.Capture);
                continue;
            }

            if (result.Outcome is PaymentOutcomes.Voided or PaymentOutcomes.Refunded or PaymentOutcomes.NothingToCompensate or PaymentOutcomes.Rejected)
            {
                return;
            }

            if (attempt < timings.CompensationRounds)
            {
                await context.CreateTimer(timings.CompensationBackoff);
                continue;
            }

            var resolved = await WaitForCaseAsync(context, orderId, InterventionKinds.CompensationExhausted, step,
                $"operation {result.OperationKey}: {result.Outcome} after {attempt} round(s), code {result.Code}");
            if (resolved.Action is CaseActions.ConfirmExternalSuccess or CaseActions.Acknowledge)
            {
                return;
            }

            attempt = 0;
        }
    }
}
