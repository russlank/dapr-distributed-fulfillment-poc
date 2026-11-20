using Dapr.Workflow;
using Fulfillment.ComputeWorker.Checks;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.OrderApi.Orders;
using Fulfillment.OrderApi.Reviews;
using Fulfillment.Primitives;
using Fulfillment.WorkflowHost.Orders;
using Xunit;

namespace Fulfillment.UnitTests;

public class FulfillOrderWorkflowTests
{
    private static readonly Guid OrderId = Guid.Parse("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b");
    private static readonly Guid ReviewId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Round2Id = Guid.Parse("66666666-7777-8888-9999-000000000000");
    private static readonly string Instance = FulfillmentWorkflow.InstanceId(OrderId);
    private static readonly CheckExecution Ran = new("compute-1", new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 18, 0, 0, 1, DateTimeKind.Utc));
    private static readonly DateTime Deadline = new(2026, 11, 18, 0, 10, 0, DateTimeKind.Utc);

    private static readonly ValidatedLine[] TwoLines = [new(1, "SKU-1", 2), new(2, "SKU-2", 1)];

    /// <summary>Activity results of a run in which every step succeeds; individual tests override one step.</summary>
    private static Func<string, object?, object?> Results(Func<string, object?, object?>? overrides = null, bool storedReview = false) => (name, input) =>
        overrides?.Invoke(name, input) ?? name switch
        {
            FulfillmentWorkflow.ClaimExecutionActivity => new ExecutionClaimResult(ExecutionClaimOutcomes.Proceed, "Submitted", 1),
            FulfillmentWorkflow.ValidateOrderActivity => new OrderValidationResult(StepOutcomes.Valid, "Validating", null, "c-1", TwoLines),
            FulfillmentWorkflow.PriceOrderActivity => new PriceQuote(1234, "EUR", "stub-v1", Ran),
            FulfillmentWorkflow.AssessOrderRiskActivity => new RiskAssessment(10, false, "stub-v2", Ran),
            FulfillmentWorkflow.RecordChecksActivity => new ChecksRecorded(StepOutcomes.Applied, "Validating", storedReview, 10, "stub-v2"),
            FulfillmentWorkflow.CreateReviewTaskActivity => new ReviewRound(StepOutcomes.Applied, ReviewId, 1, Deadline, TimeSpan.FromSeconds(5), "AwaitingApproval"),
            FulfillmentWorkflow.BeginFulfillmentActivity => new StepResult(StepOutcomes.Applied, "k", "Reserving"),
            FulfillmentWorkflow.ApplyStubStepActivity => new StepResult(StepOutcomes.Applied, "k", "Reserving"),
            FulfillmentWorkflow.CompleteStubFulfillmentActivity => new StepResult(StepOutcomes.Applied, "k", "Completed"),
            FulfillmentWorkflow.CloseUnfulfilledOrderActivity => new StepResult(StepOutcomes.Applied, "k", ((CloseUnfulfilledRequest)input!).Status),
            InventoryActivities.ReserveOrderLine => new ReserveLineResult(InventoryOutcomes.Reserved, Guid.NewGuid(), "W1", null, ["W1:Reserved"], "inventory-1"),
            InventoryActivities.ConsumeOrderHolds => new ConsumeHoldsResult(InventoryOutcomes.Consumed, 2, "inventory-1"),
            InventoryActivities.ReleaseOrderHolds => new ReleaseHoldsResult(1, 0, "inventory-1"),
            InventoryActivities.ExtendOrderHolds => new ExtendHoldsResult(InventoryOutcomes.Extended, AuthExpires, 1_800_000, false, "inventory-1"),
            PaymentActivities.ExecutePayment => Paid((PaymentOperationRequest)input!),
            OrderLifecycleActivities.AdvanceFulfillment => new StepResult(StepOutcomes.Applied, "k", ((AdvanceFulfillmentRequest)input!).ToStatus),
            OrderLifecycleActivities.BeginDispatch => new BeginDispatchResult(DispatchOutcomes.Started, "Packing"),
            Fulfillment.Contracts.Shipping.ShippingActivities.DispatchShipment => new Fulfillment.Contracts.Shipping.DispatchShipmentResult(
                Fulfillment.Contracts.Shipping.DispatchResults.Dispatched, OrderId, AuthExpires, 1, "shipping-1"),
            Fulfillment.Contracts.Shipping.ShippingActivities.GetShipmentStatus => new Fulfillment.Contracts.Shipping.ShipmentStatusResult(
                OrderId, "Delivered", "Delivered", AuthExpires, false),
            OrderLifecycleActivities.FinalizeDelivered => new StepResult(StepOutcomes.Applied, "k", "Completed"),
            _ => throw new InvalidOperationException(name),
        };

    private static readonly DateTime AuthExpires = new(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The successful outcome of each payment step.</summary>
    private static PaymentOperationResult Paid(PaymentOperationRequest r, string? outcome = null, string? code = null) =>
        new($"{r.OrderId:N}:{r.Step}:{r.Qualifier}", outcome ?? r.Step switch
        {
            PaymentSteps.Authorize => PaymentOutcomes.Authorized,
            PaymentSteps.Capture => PaymentOutcomes.Captured,
            PaymentSteps.Void => PaymentOutcomes.Voided,
            _ => PaymentOutcomes.Refunded,
        }, code, r.Step == PaymentSteps.Authorize ? AuthExpires : null, 1, 0, false, "payment-1");

    private static string[] PaymentSteps_(FakeWorkflowContext c) =>
        [.. c.ActivityCalls.Select(a => a.Input).OfType<PaymentOperationRequest>().Select(r => r.Step)];

    /// <summary>The activities of a run after the second reservation, in order, when every step succeeds.</summary>
    private static readonly string[] AfterReservation =
    [
        PaymentActivities.ExecutePayment, OrderLifecycleActivities.AdvanceFulfillment, OrderLifecycleActivities.AdvanceFulfillment,
        FulfillmentWorkflow.ApplyStubStepActivity, FulfillmentWorkflow.ApplyStubStepActivity, OrderLifecycleActivities.BeginDispatch,
        InventoryActivities.ExtendOrderHolds, PaymentActivities.ExecutePayment, InventoryActivities.ConsumeOrderHolds,
        Fulfillment.Contracts.Shipping.ShippingActivities.DispatchShipment, OrderLifecycleActivities.AdvanceFulfillment, OrderLifecycleActivities.AdvanceFulfillment,
        Fulfillment.Contracts.Shipping.ShippingActivities.GetShipmentStatus, OrderLifecycleActivities.FinalizeDelivered,
    ];

    /// <summary>Resolutions returned one after another (the last one repeats).</summary>
    private static Func<string, object?, object?> Resolutions(params ReviewResolution[] sequence)
    {
        var next = 0;
        return (name, _) => name == FulfillmentWorkflow.ResolveReviewActivity ? sequence[Math.Min(next++, sequence.Length - 1)] : null;
    }

    private static ReviewResolution Pending(bool passed = false) => new(ReviewStates.Pending, passed, "AwaitingApproval", false);

    private static readonly ReviewResolution Approved = new(ReviewStates.Approved, false, "AwaitingApproval", false);

    private static string[] Names(FakeWorkflowContext c) => [.. c.ActivityCalls.Select(a => a.Name)];

    private static string[] Wakes(FakeWorkflowContext c) => [.. c.ActivityCalls.Select(a => a.Input).OfType<ResolveReviewRequest>().Select(r => r.Wake)];

    [Fact]
    public async Task Run_NoReviewRequired_RunsEveryStepOnItsOwningAppAndCompletes()
    {
        var context = new FakeWorkflowContext(Instance) { ActivityResult = Results() };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(
            [
                (FulfillmentWorkflow.ClaimExecutionActivity, AppIds.OrderApi),
                (FulfillmentWorkflow.ValidateOrderActivity, AppIds.OrderApi),
                (FulfillmentWorkflow.PriceOrderActivity, AppIds.ComputeWorker),
                (FulfillmentWorkflow.AssessOrderRiskActivity, AppIds.ComputeWorker),
                (FulfillmentWorkflow.RecordChecksActivity, AppIds.OrderApi),
                (FulfillmentWorkflow.BeginFulfillmentActivity, AppIds.OrderApi),
                (InventoryActivities.ReserveOrderLine, AppIds.InventoryService),
                (InventoryActivities.ReserveOrderLine, AppIds.InventoryService),
                (PaymentActivities.ExecutePayment, AppIds.PaymentService),
                (OrderLifecycleActivities.AdvanceFulfillment, AppIds.OrderApi),
                (OrderLifecycleActivities.AdvanceFulfillment, AppIds.OrderApi),
                (FulfillmentWorkflow.ApplyStubStepActivity, AppIds.OrderApi),
                (FulfillmentWorkflow.ApplyStubStepActivity, AppIds.OrderApi),
                (OrderLifecycleActivities.BeginDispatch, AppIds.OrderApi),
                (InventoryActivities.ExtendOrderHolds, AppIds.InventoryService),
                (PaymentActivities.ExecutePayment, AppIds.PaymentService),
                (InventoryActivities.ConsumeOrderHolds, AppIds.InventoryService),
                (Fulfillment.Contracts.Shipping.ShippingActivities.DispatchShipment, AppIds.ShippingService),
                (OrderLifecycleActivities.AdvanceFulfillment, AppIds.OrderApi),
                (OrderLifecycleActivities.AdvanceFulfillment, AppIds.OrderApi),
                (Fulfillment.Contracts.Shipping.ShippingActivities.GetShipmentStatus, AppIds.ShippingService),
                (OrderLifecycleActivities.FinalizeDelivered, AppIds.OrderApi),
            ],
            context.ActivityCalls.Select(c => (c.Name, c.TargetAppId!)).ToArray());
        Assert.Equal(
            [new StubStepRequest(OrderId, "stub-fulfil", "line-1"), new StubStepRequest(OrderId, "stub-fulfil", "line-2")],
            context.ActivityCalls.Select(c => c.Input).OfType<StubStepRequest>().ToArray());
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Completed, "Completed", 1), result);
        Assert.Empty(context.Timers);
        Assert.DoesNotContain(FulfillmentWorkflow.CreateReviewTaskActivity, Names(context));
        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Capture], PaymentSteps_(context));
        var capture = context.ActivityCalls.Select(c => c.Input).OfType<PaymentOperationRequest>().Last();
        Assert.Equal(new PaymentOperationRequest(OrderId, PaymentSteps.Capture, "1", 1234, "EUR", PaymentSteps.Authorize, "1"), capture);
        var extend = context.ActivityCalls.Select(c => c.Input).OfType<ExtendHoldsRequest>().Single();
        Assert.Equal((2, AuthExpires), (extend.ExpectedLines, extend.AuthorizationExpiresAtUtc));
    }

    [Fact]
    public async Task Run_IndependentChecks_AreBothScheduledBeforeEitherCompletes()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(),
            Deferred = new HashSet<string>([FulfillmentWorkflow.PriceOrderActivity, FulfillmentWorkflow.AssessOrderRiskActivity]),
        };

        var run = new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        // Neither check has completed, yet both are scheduled and nothing after them has started.
        Assert.False(run.IsCompleted);
        Assert.Equal(
            [FulfillmentWorkflow.ClaimExecutionActivity, FulfillmentWorkflow.ValidateOrderActivity, FulfillmentWorkflow.PriceOrderActivity, FulfillmentWorkflow.AssessOrderRiskActivity],
            Names(context));
        var inputs = context.ActivityCalls.Skip(2).Select(c => Assert.IsType<OrderCheckInput>(c.Input)).ToArray();
        Assert.All(inputs, i => Assert.Equal(TwoLines, i.Lines));

        context.Release();
        Assert.Equal(FulfillmentOutcomes.Completed, (await run).Outcome);
    }

    [Fact]
    public async Task Run_OrderAlreadyTerminal_EndsAfterTheClaimWithoutFurtherSteps()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == FulfillmentWorkflow.ClaimExecutionActivity
                ? new ExecutionClaimResult(ExecutionClaimOutcomes.AlreadyTerminal, "Completed", 1) : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([FulfillmentWorkflow.ClaimExecutionActivity], Names(context));
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Fenced, "Completed", 1), result);
    }

    [Fact]
    public async Task Run_ValidationRejects_EndsWithoutChecksOrEffects()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == FulfillmentWorkflow.ValidateOrderActivity
                ? new OrderValidationResult(StepOutcomes.Rejected, "Rejected", "DuplicateSku", "c-1", TwoLines) : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([FulfillmentWorkflow.ClaimExecutionActivity, FulfillmentWorkflow.ValidateOrderActivity], Names(context));
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Rejected, "Rejected", 1), result);
    }

    [Theory]
    [InlineData(FulfillmentWorkflow.ValidateOrderActivity, 2)]
    [InlineData(FulfillmentWorkflow.RecordChecksActivity, 5)]
    [InlineData(FulfillmentWorkflow.BeginFulfillmentActivity, 6)]
    [InlineData(FulfillmentWorkflow.ApplyStubStepActivity, 12)]
    [InlineData(OrderLifecycleActivities.FinalizeDelivered, 22)]
    public async Task Run_StepFindsTheOrderTerminal_EndsThereWithoutFurtherSteps(string step, int calls)
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name != step ? null : step switch
            {
                FulfillmentWorkflow.ValidateOrderActivity => new OrderValidationResult(StepOutcomes.AlreadyTerminal, "Completed", null, "c-1", []),
                FulfillmentWorkflow.RecordChecksActivity => new ChecksRecorded(StepOutcomes.AlreadyTerminal, "Completed", false, 0, ""),
                _ => new StepResult(StepOutcomes.AlreadyTerminal, null, "Completed"),
            }),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(calls, context.ActivityCalls.Count);
        Assert.Equal(step, context.ActivityCalls[^1].Name);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Fenced, "Completed", 1), result);
    }

    [Fact]
    public async Task Run_StoredResultRequiresNoReview_ContinuesAlthoughThisComputationSaysReview()
    {
        // The recomputed risk check disagrees with the stored one: the stored result governs.
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == FulfillmentWorkflow.AssessOrderRiskActivity ? new RiskAssessment(90, true, "stub-v9", Ran) : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
        Assert.DoesNotContain(FulfillmentWorkflow.CreateReviewTaskActivity, Names(context));
    }

    [Fact]
    public async Task Run_StoredResultRequiresReview_WaitsAlthoughThisComputationSaysNoReview()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(Resolutions(Pending(), Approved), storedReview: true),
            Events = { [ReviewSignals.EventName(ReviewId)] = new ReviewSignal(Guid.NewGuid()) },
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
        Assert.Contains(FulfillmentWorkflow.CreateReviewTaskActivity, Names(context));
    }

    [Fact]
    public async Task Run_ReviewApprovedBySignal_ResolvesFromSqlAfterTheWakeUpThenFulfils()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(Resolutions(Pending(), Approved), storedReview: true),
            Events = { [ReviewSignals.EventName(ReviewId)] = new ReviewSignal(Guid.NewGuid()) },
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(
            [
                FulfillmentWorkflow.ClaimExecutionActivity, FulfillmentWorkflow.ValidateOrderActivity, FulfillmentWorkflow.PriceOrderActivity,
                FulfillmentWorkflow.AssessOrderRiskActivity, FulfillmentWorkflow.RecordChecksActivity, FulfillmentWorkflow.CreateReviewTaskActivity,
                FulfillmentWorkflow.ResolveReviewActivity, FulfillmentWorkflow.ResolveReviewActivity, FulfillmentWorkflow.BeginFulfillmentActivity,
                InventoryActivities.ReserveOrderLine, InventoryActivities.ReserveOrderLine, .. AfterReservation,
            ],
            Names(context));
        Assert.Equal([ReviewWakes.Initial, ReviewWakes.Signal], Wakes(context));
        Assert.Equal([Deadline.AddSeconds(5)], context.Timers);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Completed, "Completed", 1), result);
    }

    [Fact]
    public async Task Run_DecisionCommittedBeforeTheWait_ContinuesWithoutWaiting()
    {
        var context = new FakeWorkflowContext(Instance) { ActivityResult = Results(Resolutions(Approved), storedReview: true) };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([ReviewWakes.Initial], Wakes(context));
        Assert.Empty(context.Timers);
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_TimerBeforeTheSqlDeadline_WaitsAgainWithoutExpiring()
    {
        // The timer fires but SQL time is still before the deadline (clock skew): wait again; the
        // approval that follows is used. Expiry is never attempted.
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Resolutions(Pending(), Pending(), Approved), storedReview: true),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([ReviewWakes.Initial, ReviewWakes.Timer, ReviewWakes.Timer], Wakes(context));
        Assert.DoesNotContain(FulfillmentWorkflow.ExpireOrEscalateReviewActivity, Names(context));
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
        // The second timer is never in the past: at least the grace after the workflow's own time.
        Assert.Equal([Deadline.AddSeconds(5), Deadline.AddSeconds(5)], context.Timers);
    }

    [Fact]
    public async Task Run_ApprovalCommittedBeforeTheDeadlineButSeenAfterIt_IsApplied()
    {
        // The timer wins the race; SQL already says Approved (decided in time): no expiry.
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Resolutions(Pending(), Approved with { DeadlinePassed = true }), storedReview: true),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([ReviewWakes.Initial, ReviewWakes.Timer], Wakes(context));
        Assert.DoesNotContain(FulfillmentWorkflow.ExpireOrEscalateReviewActivity, Names(context));
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_RoundExpires_OpensTheNextRoundAndWaitsForIt()
    {
        var round2 = new ReviewRound(StepOutcomes.Applied, Round2Id, 2, Deadline.AddMinutes(10), TimeSpan.FromSeconds(5), "AwaitingApproval");
        var resolutions = Resolutions(Pending(), Pending(passed: true), Pending(), Approved);
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results((name, input) => name == FulfillmentWorkflow.ExpireOrEscalateReviewActivity
                ? new ExpiryResult(ExpiryOutcomes.NextRound, round2, "AwaitingApproval")
                : resolutions(name, input), storedReview: true),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        var resolved = context.ActivityCalls.Select(a => a.Input).OfType<ResolveReviewRequest>().ToArray();
        Assert.Equal([ReviewId, ReviewId, Round2Id, Round2Id], resolved.Select(r => r.ReviewId));
        Assert.Equal([ReviewWakes.Initial, ReviewWakes.Timer, ReviewWakes.Initial, ReviewWakes.Timer], resolved.Select(r => r.Wake));
        Assert.Equal(new ExpireReviewRequest(OrderId, ReviewId), Assert.Single(context.ActivityCalls.Select(a => a.Input).OfType<ExpireReviewRequest>()));
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_LastRoundExpires_EndsRejectedWithoutFulfillment()
    {
        var resolutions = Resolutions(Pending(), Pending(passed: true));
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results((name, input) => name == FulfillmentWorkflow.ExpireOrEscalateReviewActivity
                ? new ExpiryResult(ExpiryOutcomes.FinalExpiry, null, "Rejected")
                : resolutions(name, input), storedReview: true),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Rejected, "Rejected", 1), result);
        Assert.DoesNotContain(FulfillmentWorkflow.BeginFulfillmentActivity, Names(context));
    }

    [Fact]
    public async Task Run_ExpiryFindsTheReviewDecided_ResolvesAgainAndUsesTheDecision()
    {
        var resolutions = Resolutions(Pending(), Pending(passed: true), Approved);
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results((name, input) => name == FulfillmentWorkflow.ExpireOrEscalateReviewActivity
                ? new ExpiryResult(ExpiryOutcomes.AlreadyDecided, null, "AwaitingApproval")
                : resolutions(name, input), storedReview: true),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([ReviewWakes.Initial, ReviewWakes.Timer, ReviewWakes.Recheck], Wakes(context));
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_ReviewRejected_EndsRejectedWithoutFulfillment()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(Resolutions(new ReviewResolution(ReviewStates.Rejected, false, "Rejected", true)), storedReview: true),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Rejected, "Rejected", 1), result);
        Assert.DoesNotContain(FulfillmentWorkflow.BeginFulfillmentActivity, Names(context));
    }

    [Fact]
    public async Task Run_Reservation_LinesInSkuOrderWithTheirQuantities()
    {
        ValidatedLine[] lines = [new(1, "SKU-9", 2), new(2, "SKU-10", 3)];
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == FulfillmentWorkflow.ValidateOrderActivity
                ? new OrderValidationResult(StepOutcomes.Valid, "Validating", null, "c-1", lines) : null),
        };

        await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        // Ordinal SKU order: "SKU-10" before "SKU-9"; consumption expects both lines.
        Assert.Equal([new ReserveLineRequest(OrderId, 2, "SKU-10", 3), new ReserveLineRequest(OrderId, 1, "SKU-9", 2)],
            context.ActivityCalls.Select(c => c.Input).OfType<ReserveLineRequest>().ToArray());
        Assert.Equal(new ConsumeHoldsRequest(OrderId, 2), context.ActivityCalls.Select(c => c.Input).OfType<ConsumeHoldsRequest>().Single());
    }

    [Fact]
    public async Task Run_LineWithoutStock_ReleasesWhatWasReservedAndRejects()
    {
        var reserves = 0;
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == InventoryActivities.ReserveOrderLine && ++reserves == 2
                ? new ReserveLineResult(InventoryOutcomes.InsufficientStock, null, null, null, ["W1:InsufficientStock", "W2:InsufficientStock"], "inventory-1") : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(
            [
                FulfillmentWorkflow.BeginFulfillmentActivity, InventoryActivities.ReserveOrderLine, InventoryActivities.ReserveOrderLine,
                InventoryActivities.ReleaseOrderHolds, FulfillmentWorkflow.CloseUnfulfilledOrderActivity,
            ],
            Names(context)[5..]);
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Rejected", "InsufficientStock"), context.ActivityCalls[^1].Input);
        Assert.Equal(new ReleaseHoldsRequest(OrderId, "InsufficientStock"), context.ActivityCalls[^2].Input);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Rejected, "Rejected", 1), result);
    }

    [Fact]
    public async Task Run_ReservationStillFailingAfterRetries_ReleasesAndCompensates()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == InventoryActivities.ReserveOrderLine
                ? throw new WorkflowTaskFailedException("failed", new WorkflowTaskFailureDetails("InvalidOperationException", "inventory unavailable", null))
                : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([InventoryActivities.ReserveOrderLine, InventoryActivities.ReleaseOrderHolds, FulfillmentWorkflow.CloseUnfulfilledOrderActivity], Names(context)[6..]);
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", "ReservationFailed"), context.ActivityCalls[^1].Input);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Compensated, "Compensated", 1), result);
    }

    private static Func<string, object?, object?> Payment(string step, string outcome, string? code = null) =>
        (name, input) => name == PaymentActivities.ExecutePayment && ((PaymentOperationRequest)input!).Step == step ? Paid((PaymentOperationRequest)input!, outcome, code) : null;

    private static Func<string, object?, object?> Both(params Func<string, object?, object?>[] overrides) =>
        (name, input) => overrides.Select(o => o(name, input)).FirstOrDefault(r => r is not null);

    private static Fulfillment.Contracts.Shipping.ShipmentStatusResult Shipment(string? current, string? terminal, bool passed = false) =>
        new(OrderId, current, terminal, AuthExpires, passed);

    private static Func<string, object?, object?> Statuses(params Fulfillment.Contracts.Shipping.ShipmentStatusResult[] sequence)
    {
        var next = 0;
        return (name, _) => name is Fulfillment.Contracts.Shipping.ShippingActivities.GetShipmentStatus or Fulfillment.Contracts.Shipping.ShippingActivities.PollCarrierStatus
            ? sequence[Math.Min(next++, sequence.Length - 1)]
            : null;
    }

    private static Func<string, object?, object?> Case(string kind, string action)
    {
        var caseId = Guid.NewGuid();
        return (name, _) => name switch
        {
            OrderLifecycleActivities.OpenInterventionCase => new CaseResult(caseId, kind, CaseStates.Open, null, "ManualInterventionRequired", false, true),
            OrderLifecycleActivities.ReadInterventionCase => new CaseResult(caseId, kind, CaseStates.Resolved, action, "AwaitingDelivery", false, false),
            _ => null,
        };
    }

    [Fact]
    public async Task Run_CarrierSignalWakesTheWait_ShippingRecordDecides_ThenFinalizes()
    {
        var shipment = Fulfillment.Contracts.Shipping.CarrierSignals.EventName(OrderId);
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(Statuses(Shipment("InTransit", null), Shipment("Delivered", "Delivered"))),
            Events = { [shipment] = new object() },
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(2, Names(context).Count(n => n == Fulfillment.Contracts.Shipping.ShippingActivities.GetShipmentStatus));
        Assert.Equal(OrderLifecycleActivities.FinalizeDelivered, Names(context)[^1]);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Completed, "Completed", 1), result);
    }

    [Fact]
    public async Task Run_DeliveryException_OpensACase_ConfirmedSuccessFinalizes()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Both(Statuses(Shipment("Exception", "Exception")), Case(InterventionKinds.DeliveryException, CaseActions.ConfirmExternalSuccess))),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(new OpenCaseRequest(OrderId, InterventionKinds.DeliveryException, "delivery-1",
            $"shipment {OrderId}: current Exception, terminal Exception, deadline {AuthExpires:O}"), context.ActivityCalls.Select(c => c.Input).OfType<OpenCaseRequest>().Single());
        Assert.Equal(OrderLifecycleActivities.FinalizeDelivered, Names(context)[^1]);
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_DeliveryExceptionNotConfirmed_EndsWithoutFinalizing()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Both(Statuses(Shipment("Exception", "Exception")), Case(InterventionKinds.DeliveryException, CaseActions.Acknowledge))),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.DoesNotContain(OrderLifecycleActivities.FinalizeDelivered, Names(context));
        Assert.Equal(FulfillmentOutcomes.Fenced, result.Outcome);
    }

    [Fact]
    public async Task Run_DeadlinePassedWithoutTerminalStatus_PollsOnceThenOpensDeliveryOverdue()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Both(Statuses(Shipment("InTransit", null, passed: true)), Case(InterventionKinds.DeliveryOverdue, CaseActions.ConfirmExternalSuccess))),
        };

        await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(1, Names(context).Count(n => n == Fulfillment.Contracts.Shipping.ShippingActivities.PollCarrierStatus));
        Assert.Equal(InterventionKinds.DeliveryOverdue, context.ActivityCalls.Select(c => c.Input).OfType<OpenCaseRequest>().Single().Kind);
    }

    [Fact]
    public async Task Run_DispatchUndeterminable_OpensACaseAndDispatchesAgainWithTheSameShipment()
    {
        var dispatches = 0;
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Both(
                (name, _) => name == Fulfillment.Contracts.Shipping.ShippingActivities.DispatchShipment && ++dispatches == 1
                    ? new Fulfillment.Contracts.Shipping.DispatchShipmentResult(Fulfillment.Contracts.Shipping.DispatchResults.Ambiguous, OrderId, null, 4, "shipping-1")
                    : null,
                Case(InterventionKinds.AmbiguousDispatch, CaseActions.ConfirmExternalFailure))),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(2, dispatches);
        Assert.Equal(InterventionKinds.AmbiguousDispatch, context.ActivityCalls.Select(c => c.Input).OfType<OpenCaseRequest>().Single().Kind);
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_AuthorizationDeclined_RejectsAfterReleasing_WithoutAnyCompensatingPayment()
    {
        var context = new FakeWorkflowContext(Instance) { ActivityResult = Results(Payment(PaymentSteps.Authorize, PaymentOutcomes.Declined)) };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentActivities.ExecutePayment, InventoryActivities.ReleaseOrderHolds, FulfillmentWorkflow.CloseUnfulfilledOrderActivity], Names(context)[^3..]);
        Assert.Equal([PaymentSteps.Authorize], PaymentSteps_(context));
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Rejected", "PaymentDeclined"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Run_ProviderUnavailable_VoidsTheAuthorizationKeyAndCompensates()
    {
        // Nothing can have committed; the void by key still prohibits a late authorization.
        var context = new FakeWorkflowContext(Instance) { ActivityResult = Results(Payment(PaymentSteps.Authorize, PaymentOutcomes.Unavailable)) };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Void], PaymentSteps_(context));
        Assert.Equal(new PaymentOperationRequest(OrderId, PaymentSteps.Void, "1", 1234, "EUR", PaymentSteps.Authorize, "1", true),
            context.ActivityCalls.Select(c => c.Input).OfType<PaymentOperationRequest>().Last());
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", "PaymentProviderUnavailable"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Compensated, result.Outcome);
    }

    [Fact]
    public async Task Run_CancellationRecordedBeforeDispatch_VoidsReleasesAndCancels_NoCapture()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == OrderLifecycleActivities.BeginDispatch ? new BeginDispatchResult(DispatchOutcomes.CancelRequested, "Packing") : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Void], PaymentSteps_(context));
        Assert.DoesNotContain(InventoryActivities.ExtendOrderHolds, Names(context));
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Cancelled", "CancellationRequested"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Cancelled, result.Outcome);
    }

    [Theory]
    [InlineData(InventoryOutcomes.HoldExpired, 1_800_000)]
    [InlineData(InventoryOutcomes.InsufficientValidity, 1_000)]
    [InlineData(InventoryOutcomes.Extended, 1_000)]
    public async Task Run_HoldsCannotLastThroughTheCapture_VoidsInsteadOfCapturing(string outcome, long remaining)
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == InventoryActivities.ExtendOrderHolds ? new ExtendHoldsResult(outcome, null, remaining, false, "inventory-1") : null),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Void], PaymentSteps_(context));
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", "ReservationExpired"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Compensated, result.Outcome);
    }

    [Fact]
    public async Task Run_CaptureRejected_VoidsAndCompensates()
    {
        var context = new FakeWorkflowContext(Instance) { ActivityResult = Results(Payment(PaymentSteps.Capture, PaymentOutcomes.Rejected, "AuthorizationExpired")) };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Capture, PaymentSteps.Void], PaymentSteps_(context));
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", "PaymentCaptureFailed"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Compensated, result.Outcome);
    }

    [Fact]
    public async Task Run_VoidFindsTheAuthorizationCaptured_RefundsTheCapture()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(Both(Payment(PaymentSteps.Capture, PaymentOutcomes.Unavailable), Payment(PaymentSteps.Void, PaymentOutcomes.Rejected, "AlreadyCaptured"))),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Capture, PaymentSteps.Void, PaymentSteps.Refund], PaymentSteps_(context));
        Assert.Equal(FulfillmentOutcomes.Compensated, result.Outcome);
    }

    [Fact]
    public async Task Run_AmbiguousAuthorization_WaitsForTheCaseThenAdoptsTheProviderRecord()
    {
        var caseId = Guid.NewGuid();
        var reads = 0;
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results(Both(
                Payment(PaymentSteps.Authorize, PaymentOutcomes.Ambiguous, "StatusUndetermined"),
                (name, input) => name switch
                {
                    OrderLifecycleActivities.OpenInterventionCase => new CaseResult(caseId, InterventionKinds.AmbiguousPayment, CaseStates.Open, null, "ManualInterventionRequired", false, true),
                    OrderLifecycleActivities.ReadInterventionCase => ++reads == 1
                        ? new CaseResult(caseId, InterventionKinds.AmbiguousPayment, CaseStates.Resolved, CaseActions.ConfirmExternalSuccess, "Reserving", false, false)
                        : null,
                    PaymentActivities.ReconcilePayment => Paid((PaymentOperationRequest)input!, PaymentOutcomes.Authorized),
                    _ => null,
                })),
            Events = { [InterventionSignals.EventName(caseId)] = new object() },
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(
            [PaymentActivities.ExecutePayment, OrderLifecycleActivities.OpenInterventionCase, OrderLifecycleActivities.ReadInterventionCase, PaymentActivities.ReconcilePayment,
                OrderLifecycleActivities.AdvanceFulfillment],
            Names(context)[8..13]);
        Assert.Equal(new OpenCaseRequest(OrderId, InterventionKinds.AmbiguousPayment, "authorize-1",
            $"operation {OrderId:N}:authorize:1: 1 send(s), 0 status query(ies), code StatusUndetermined"), context.ActivityCalls[9].Input);
        Assert.Equal(FulfillmentOutcomes.Completed, result.Outcome);
    }

    [Fact]
    public async Task Run_CompensationStaysUnresolved_RetriesInRoundsThenWaitsForACase()
    {
        var caseId = Guid.NewGuid();
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Both(
                Payment(PaymentSteps.Authorize, PaymentOutcomes.Unavailable),
                Payment(PaymentSteps.Void, PaymentOutcomes.Ambiguous, "BudgetExhausted"),
                (name, _) => name switch
                {
                    OrderLifecycleActivities.OpenInterventionCase => new CaseResult(caseId, InterventionKinds.CompensationExhausted, CaseStates.Open, null, "ManualInterventionRequired", false, true),
                    OrderLifecycleActivities.ReadInterventionCase => new CaseResult(caseId, InterventionKinds.CompensationExhausted, CaseStates.Resolved, CaseActions.ConfirmExternalSuccess, "Reserving", false, false),
                    _ => null,
                })),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal([PaymentSteps.Authorize, PaymentSteps.Void, PaymentSteps.Void, PaymentSteps.Void], PaymentSteps_(context));
        Assert.Contains(new OpenCaseRequest(OrderId, InterventionKinds.CompensationExhausted, "void", $"operation {OrderId:N}:void:1: Ambiguous after 3 round(s), code BudgetExhausted"),
            context.ActivityCalls.Select(c => c.Input));
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", "PaymentProviderUnavailable"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Compensated, result.Outcome);
    }

    [Fact]
    public async Task Run_ReleaseKeepsFailing_OpensACompensationCaseInsteadOfFailingTheRun()
    {
        var caseId = Guid.NewGuid();
        var releases = 0;
        var context = new FakeWorkflowContext(Instance)
        {
            TimersFireImmediately = true,
            ActivityResult = Results(Both(
                Payment(PaymentSteps.Authorize, PaymentOutcomes.Declined),
                (name, _) => name switch
                {
                    InventoryActivities.ReleaseOrderHolds when ++releases == 1 => throw new WorkflowTaskFailedException("failed", new WorkflowTaskFailureDetails("SqlException", "timeout", null)),
                    OrderLifecycleActivities.OpenInterventionCase => new CaseResult(caseId, InterventionKinds.CompensationExhausted, CaseStates.Open, null, "ManualInterventionRequired", false, true),
                    OrderLifecycleActivities.ReadInterventionCase => new CaseResult(caseId, InterventionKinds.CompensationExhausted, CaseStates.Resolved, CaseActions.Acknowledge, "Reserving", false, false),
                    _ => null,
                })),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(2, releases);
        Assert.Contains(OrderLifecycleActivities.OpenInterventionCase, Names(context));
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Rejected", "PaymentDeclined"), context.ActivityCalls[^1].Input);
        Assert.Equal(FulfillmentOutcomes.Rejected, result.Outcome);
    }

    [Theory]
    [InlineData(InventoryOutcomes.HoldExpired, "ReservationExpired")]
    [InlineData(InventoryOutcomes.Incomplete, "ReservationExpired")]
    [InlineData(null, "ReservationFailed")]
    public async Task Run_ConsumptionImpossible_ReleasesAndCompensatesWithoutCompleting(string? outcome, string reason)
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name != InventoryActivities.ConsumeOrderHolds ? null
                : outcome is null ? throw new WorkflowTaskFailedException("failed", new WorkflowTaskFailureDetails("SqlException", "timeout", null))
                : new ConsumeHoldsResult(outcome, 0, "inventory-1")),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        // The capture already happened: the holds are released and the capture refunded by its key.
        Assert.Equal([InventoryActivities.ConsumeOrderHolds, InventoryActivities.ReleaseOrderHolds, PaymentActivities.ExecutePayment, FulfillmentWorkflow.CloseUnfulfilledOrderActivity], Names(context)[^4..]);
        Assert.DoesNotContain(FulfillmentWorkflow.CompleteStubFulfillmentActivity, Names(context));
        Assert.Equal(new PaymentOperationRequest(OrderId, PaymentSteps.Refund, "1", 1234, "EUR", PaymentSteps.Capture, "1", true),
            context.ActivityCalls[^2].Input);
        Assert.Equal(new CloseUnfulfilledRequest(OrderId, "Compensated", reason), context.ActivityCalls[^1].Input);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Compensated, "Compensated", 1), result);
    }

    [Fact]
    public async Task Run_SameInputTwice_ProducesTheSameActivityCallsAndInputs()
    {
        // Determinism: a replay (or a repeated run of the instance id) derives the same calls and keys.
        static FakeWorkflowContext New() => new(Instance) { TimersFireImmediately = true, ActivityResult = Results(Resolutions(Pending(), Approved), storedReview: true) };
        var first = New();
        var second = New();

        await new FulfillOrderWorkflow().RunAsync(first, new FulfillmentInput(OrderId));
        await new FulfillOrderWorkflow().RunAsync(second, new FulfillmentInput(OrderId));

        Assert.Equal(first.ActivityCalls, second.ActivityCalls);
        Assert.Equal(first.Timers, second.Timers);
    }
}

public class FulfillmentKeyTests
{
    private static readonly Guid OrderId = Guid.Parse("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b");

    [Fact]
    public void Keys_FollowTheSchemeAndAreRecognisablyStubs()
    {
        Assert.Equal("3f2b8c1e5a4d4e6f9a1b2c3d4e5f6a7b:stub-fulfil:line-3", OperationKey.For(OrderId, StubSteps.Fulfil, StubSteps.LineQualifier(3)).Value);
        Assert.Equal("3f2b8c1e5a4d4e6f9a1b2c3d4e5f6a7b:stub-complete:1", OperationKey.For(OrderId, StubSteps.Complete, "1").Value);
        Assert.Equal("3f2b8c1e5a4d4e6f9a1b2c3d4e5f6a7b:check:price", OperationKey.For(OrderId, FulfillmentChecks.Step, FulfillmentChecks.Price).Value);
        Assert.StartsWith(StubSteps.Prefix, StubSteps.Fulfil, StringComparison.Ordinal);
        Assert.StartsWith(StubSteps.Prefix, StubSteps.Complete, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "SKU-1", "SKU-2" }, null)]
    [InlineData(new[] { "SKU-1", "SKU-1" }, "DuplicateSku")]
    [InlineData(new string[0], "NoLines")]
    public void OrderRules_FirstViolation(string[] skus, string? expected)
    {
        var lines = skus.Select((s, i) => new ValidatedLine(i + 1, s, 1)).ToList();

        Assert.Equal(expected, OrderRules.FirstViolation(lines));
    }

    [Fact]
    public void StubPriceList_IsStableAndBounded()
    {
        Assert.Equal(StubPriceList.UnitPriceMinor("SKU-1"), StubPriceList.UnitPriceMinor("SKU-1"));
        Assert.InRange(StubPriceList.UnitPriceMinor("SKU-1"), 100, 9999);
        Assert.Equal((StubPriceList.UnitPriceMinor("A") * 2) + StubPriceList.UnitPriceMinor("B"),
            StubPriceList.TotalMinor([new ValidatedLine(1, "A", 2), new ValidatedLine(2, "B", 1)]));
    }

    [Theory]
    [InlineData(100_000, null, false)]
    [InlineData(100_000, 100_000L, true)]
    [InlineData(99_999, 100_000L, false)]
    public void StubRiskRule_ReviewFromTheOrderValueThreshold(long value, long? threshold, bool expected)
    {
        Assert.Equal(expected, StubRiskRule.ReviewRequired(value, threshold));
    }

    [Fact]
    public void StubPriceList_MatchesTheScriptsCopy()
    {
        // scripts/lib.sh (review_sku) computes the same prices to build orders that need a review.
        Assert.Equal((6784, 4172), (StubPriceList.UnitPriceMinor("SKU-R1"), StubPriceList.UnitPriceMinor("SKU-1")));
        Assert.True(StubPriceList.UnitPriceMinor("SKU-R1") * 100 >= 100_000);
    }

    [Fact]
    public void ComputeCheckOptions_DefaultThresholdIsTheOwnerDecision()
    {
        Assert.Equal(100_000, new ComputeCheckOptions().ReviewAmountThresholdMinor);
    }
}

public class ReviewProtocolTests
{
    private static readonly Guid Review = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(RaiseOutcomeKind.Raised, "Started", "Forwarded", "Forwarded", null)]
    [InlineData(RaiseOutcomeKind.Terminal, "Closed", "Discarded", "Discarded", "WorkflowTerminal")]
    [InlineData(RaiseOutcomeKind.NotFound, "Started", "Stuck", "Stuck", "WorkflowMissing")]
    [InlineData(RaiseOutcomeKind.NotRunning, "Started", "NotRunning", null, "WorkflowNotRunning")]
    [InlineData(RaiseOutcomeKind.Ambiguous, "Started", "Ambiguous", null, "RaiseAmbiguous")]
    [InlineData(RaiseOutcomeKind.Rejected, "Started", "Rejected", null, "RaiseRejected")]
    public void SignalDispatcher_DecidesFromTheWorkflowHostAnswer(RaiseOutcomeKind kind, string startState, string result, string? state, string? code)
    {
        // Only a raise to a running instance marks a signal delivered; an attempt alone never does.
        Assert.Equal((result, state, code), SignalDispatcher.Decide(new RaiseOutcome(kind, null, 1, 200, null), startState));
    }

    [Fact]
    public void DecisionHash_CoversEveryFieldAndTheReview()
    {
        var d = new Fulfillment.Contracts.Reviews.SubmitDecisionRequest(Guid.NewGuid(), "operator-1", "approve", null);

        Assert.Equal(ReviewDecisions.Hash(Review, d), ReviewDecisions.Hash(Review, d with { }));
        Assert.NotEqual(ReviewDecisions.Hash(Review, d), ReviewDecisions.Hash(Review, d with { Outcome = "reject" }));
        Assert.NotEqual(ReviewDecisions.Hash(Review, d), ReviewDecisions.Hash(Review, d with { OperatorId = "operator-2" }));
        Assert.NotEqual(ReviewDecisions.Hash(Review, d), ReviewDecisions.Hash(Review, d with { Comment = "ok" }));
        Assert.NotEqual(ReviewDecisions.Hash(Review, d), ReviewDecisions.Hash(Guid.NewGuid(), d));
        Assert.Equal(32, ReviewDecisions.Hash(Review, d).Length);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "operator-1", "approve", null, false)]
    [InlineData("8b0c5a52-0b31-4d2e-9a43-0d3a3b1f5a10", "", "approve", null, false)]
    [InlineData("8b0c5a52-0b31-4d2e-9a43-0d3a3b1f5a10", "op 1", "approve", null, false)]
    [InlineData("8b0c5a52-0b31-4d2e-9a43-0d3a3b1f5a10", "operator-1", "escalate", null, false)]
    [InlineData("8b0c5a52-0b31-4d2e-9a43-0d3a3b1f5a10", "operator-1", "approve", null, true)]
    [InlineData("8b0c5a52-0b31-4d2e-9a43-0d3a3b1f5a10", "operator-1", "reject", "not plausible", true)]
    public void DecisionContract_IsChecked(string id, string op, string outcome, string? comment, bool valid)
    {
        Assert.Equal(valid, ReviewEndpoints.Invalid(new Fulfillment.Contracts.Reviews.SubmitDecisionRequest(Guid.Parse(id), op, outcome, comment)) is null);
    }

    [Fact]
    public void ReviewOptions_DrillProfileIsShortAndOperatorsAreListed()
    {
        var drill = new ReviewOptions();
        ReviewOptions.ForEnvironment(drill, "drill");
        var demo = new ReviewOptions();
        ReviewOptions.ForEnvironment(demo, "local");

        Assert.Equal((TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), 2), (drill.Deadline, drill.TimerGrace, drill.Rounds));
        Assert.Equal((TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5)), (demo.Deadline, demo.TimerGrace));
        Assert.True(demo.IsOperator("operator-2"));
        Assert.False(demo.IsOperator("customer-1"));
    }
}

public class StartAcknowledgementTests
{
    [Theory]
    [InlineData("Closed", 1, 1, FencedUpdateOutcome.AlreadyClosed)]
    [InlineData("Closed", 2, 1, FencedUpdateOutcome.LeaseLost)]
    [InlineData("Started", 2, 1, FencedUpdateOutcome.LeaseLost)]
    [InlineData("Pending", 3, 1, FencedUpdateOutcome.LeaseLost)]
    [InlineData("Started", 1, 1, FencedUpdateOutcome.Unexpected)]
    [InlineData("Pending", 1, 1, FencedUpdateOutcome.Unexpected)]
    [InlineData(null, 0, 1, FencedUpdateOutcome.Unexpected)]
    public void Classify_DistinguishesLostLeaseFromClosedAndUnexpected(string? state, int attempts, int claimAttempt, FencedUpdateOutcome expected)
    {
        Assert.Equal(expected, FencedUpdates.Classify(state, attempts, claimAttempt));
    }

    [Theory]
    [InlineData(FencedUpdateOutcome.Applied, "Started")]
    [InlineData(FencedUpdateOutcome.LeaseLost, "LeaseLost")]
    [InlineData(FencedUpdateOutcome.AlreadyClosed, "AlreadyClosed")]
    [InlineData(FencedUpdateOutcome.Unexpected, "Unexpected")]
    public void Result_MapsTheFencedUpdate(FencedUpdateOutcome update, string expected)
    {
        Assert.Equal(expected, WorkflowStartDispatcher.Result(update, StartAttemptResults.Started));
    }
}
