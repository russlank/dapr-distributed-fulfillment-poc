using Fulfillment.ComputeWorker.Checks;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.OrderApi.Orders;
using Fulfillment.Primitives;
using Fulfillment.WorkflowHost.Orders;
using Xunit;

namespace Fulfillment.UnitTests;

public class FulfillOrderWorkflowTests
{
    private static readonly Guid OrderId = Guid.Parse("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b");
    private static readonly string Instance = FulfillmentWorkflow.InstanceId(OrderId);
    private static readonly CheckExecution Ran = new("compute-1", new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 18, 0, 0, 1, DateTimeKind.Utc));

    private static readonly ValidatedLine[] TwoLines = [new(1, "SKU-1", 2), new(2, "SKU-2", 1)];

    /// <summary>Activity results of a run in which every step succeeds; individual tests override one step.</summary>
    private static Func<string, object?, object?> Results(Func<string, object?, object?>? overrides = null) => (name, input) =>
        overrides?.Invoke(name, input) ?? name switch
        {
            FulfillmentWorkflow.ClaimExecutionActivity => new ExecutionClaimResult(ExecutionClaimOutcomes.Proceed, "Submitted", 1),
            FulfillmentWorkflow.ValidateOrderActivity => new OrderValidationResult(StepOutcomes.Valid, "Validating", null, "c-1", TwoLines),
            FulfillmentWorkflow.PriceOrderActivity => new PriceQuote(1234, "EUR", "stub-v1", Ran),
            FulfillmentWorkflow.AssessOrderRiskActivity => new RiskAssessment(10, false, "stub-v1", Ran),
            FulfillmentWorkflow.RecordChecksActivity => new StepResult(StepOutcomes.Applied, null, "Validating"),
            FulfillmentWorkflow.ApplyStubStepActivity => new StepResult(StepOutcomes.Applied, "k", "Validating"),
            FulfillmentWorkflow.CompleteStubFulfillmentActivity => new StepResult(StepOutcomes.Applied, "k", "Completed"),
            _ => throw new InvalidOperationException(name),
        };

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
                (FulfillmentWorkflow.ApplyStubStepActivity, AppIds.OrderApi),
                (FulfillmentWorkflow.ApplyStubStepActivity, AppIds.OrderApi),
                (FulfillmentWorkflow.CompleteStubFulfillmentActivity, AppIds.OrderApi),
            ],
            context.ActivityCalls.Select(c => (c.Name, c.TargetAppId!)).ToArray());
        Assert.Equal(
            [new StubStepRequest(OrderId, "stub-fulfil", "line-1"), new StubStepRequest(OrderId, "stub-fulfil", "line-2")],
            context.ActivityCalls.Select(c => c.Input).OfType<StubStepRequest>().ToArray());
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Completed, "Completed", 1), result);
        Assert.Empty(context.Timers);
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
            context.ActivityCalls.Select(c => c.Name).ToArray());
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

        Assert.Equal([FulfillmentWorkflow.ClaimExecutionActivity], context.ActivityCalls.Select(c => c.Name).ToArray());
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

        Assert.Equal([FulfillmentWorkflow.ClaimExecutionActivity, FulfillmentWorkflow.ValidateOrderActivity], context.ActivityCalls.Select(c => c.Name).ToArray());
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Rejected, "Rejected", 1), result);
    }

    [Theory]
    [InlineData(FulfillmentWorkflow.ValidateOrderActivity, 2)]
    [InlineData(FulfillmentWorkflow.RecordChecksActivity, 5)]
    [InlineData(FulfillmentWorkflow.ApplyStubStepActivity, 6)]
    [InlineData(FulfillmentWorkflow.CompleteStubFulfillmentActivity, 8)]
    public async Task Run_StepFindsTheOrderTerminal_EndsThereWithoutFurtherSteps(string step, int calls)
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name != step ? null : step == FulfillmentWorkflow.ValidateOrderActivity
                ? new OrderValidationResult(StepOutcomes.AlreadyTerminal, "Completed", null, "c-1", [])
                : new StepResult(StepOutcomes.AlreadyTerminal, null, "Completed")),
        };

        var result = await new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId));

        Assert.Equal(calls, context.ActivityCalls.Count);
        Assert.Equal(step, context.ActivityCalls[^1].Name);
        Assert.Equal(new FulfillmentResult(FulfillmentOutcomes.Fenced, "Completed", 1), result);
    }

    [Fact]
    public async Task Run_ReviewRequired_FailsBeforeAnyFulfillmentStepInsteadOfAssumingApproval()
    {
        var context = new FakeWorkflowContext(Instance)
        {
            ActivityResult = Results((name, _) => name == FulfillmentWorkflow.AssessOrderRiskActivity ? new RiskAssessment(90, true, "stub-v1", Ran) : null),
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => new FulfillOrderWorkflow().RunAsync(context, new FulfillmentInput(OrderId)));

        Assert.DoesNotContain(context.ActivityCalls, c => c.Name is FulfillmentWorkflow.ApplyStubStepActivity or FulfillmentWorkflow.CompleteStubFulfillmentActivity);
    }

    [Fact]
    public async Task Run_SameInputTwice_ProducesTheSameActivityCallsAndInputs()
    {
        // Determinism: a replay (or a repeated run of the instance id) derives the same calls and keys.
        var first = new FakeWorkflowContext(Instance) { ActivityResult = Results() };
        var second = new FakeWorkflowContext(Instance) { ActivityResult = Results() };

        await new FulfillOrderWorkflow().RunAsync(first, new FulfillmentInput(OrderId));
        await new FulfillOrderWorkflow().RunAsync(second, new FulfillmentInput(OrderId));

        Assert.Equal(first.ActivityCalls, second.ActivityCalls);
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
    [InlineData(50, null, false)]
    [InlineData(50, 50, true)]
    [InlineData(49, 50, false)]
    public void StubRiskRule_ReviewOnlyFromAConfiguredThreshold(int score, int? threshold, bool expected)
    {
        Assert.Equal(expected, StubRiskRule.ReviewRequired(score, threshold));
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
