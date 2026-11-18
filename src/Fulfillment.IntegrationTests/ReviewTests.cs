using System.Net;
using System.Net.Http.Json;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Orders;
using Fulfillment.OrderApi.Reviews;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// Reviews against SQL Server: the stored risk result as the authority, idempotent review rounds,
/// the decision protocol through the real endpoint (contract, idempotency, concurrency, deadline by
/// SQL time, rollback, immutability), resolution and expiry. Assertions read the rows.
/// </summary>
public sealed class ReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;
    private HttpClient operators = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("review");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
        operators = api.CreateOperatorClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        operators.Dispose();
        await api.DisposeAsync();
    }

    [Fact]
    public async Task NoReviewOrder_EntersFulfillmentWithoutAnyReviewTask()
    {
        var orderId = await AcceptAsync();

        await Fulfilling.EnterFulfillmentAsync(Fulfilling.Steps(db), orderId, TestContext.Current.CancellationToken);

        Assert.Equal("Reserving", (await Fulfilling.SnapshotAsync(db, orderId)).Status);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.ReviewTask WHERE OrderId = @o", ("@o", orderId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reviewing.Steps(db).CreateAsync(new CreateReviewRequest(orderId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoredRiskResult_GovernsAlthoughTheReplayDisagrees_NoReviewFirst()
    {
        var orderId = await AcceptAsync();
        var steps = Fulfilling.Steps(db);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);

        var first = await steps.RecordChecksAsync(Fulfilling.Checks(orderId, review: false, version: "stub-v2"), TestContext.Current.CancellationToken);
        var replay = await steps.RecordChecksAsync(Fulfilling.Checks(orderId, score: 95, review: true, version: "stub-v9"), TestContext.Current.CancellationToken);

        Assert.Equal((false, 10, "stub-v2"), (first.ReviewRequired, first.RiskScore, first.RiskRuleVersion));
        Assert.Equal((StepOutcomes.AlreadyApplied, false, 10, "stub-v2"), (replay.Outcome, replay.ReviewRequired, replay.RiskScore, replay.RiskRuleVersion));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reviewing.Steps(db).CreateAsync(new CreateReviewRequest(orderId), TestContext.Current.CancellationToken));
        Assert.Equal(StepOutcomes.Applied, (await steps.BeginFulfillmentAsync(new BeginFulfillmentRequest(orderId), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task StoredRiskResult_GovernsAlthoughTheReplayDisagrees_ReviewFirst()
    {
        var orderId = await AcceptAsync();
        var steps = Fulfilling.Steps(db);
        await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);

        await steps.RecordChecksAsync(Fulfilling.Checks(orderId, review: true), TestContext.Current.CancellationToken);
        var replay = await steps.RecordChecksAsync(Fulfilling.Checks(orderId, review: false, version: "stub-v9"), TestContext.Current.CancellationToken);

        Assert.True(replay.ReviewRequired);
        // The review cannot be skipped: entering the fulfillment from Validating is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => steps.BeginFulfillmentAsync(new BeginFulfillmentRequest(orderId), TestContext.Current.CancellationToken));
        Assert.Equal("Validating", (await Fulfilling.SnapshotAsync(db, orderId)).Status);
    }

    [Fact]
    public async Task CreateReview_RepeatedAndConcurrent_OneRoundOneTransition()
    {
        var orderId = await AcceptAsync();
        var first = await Reviewing.OpenAsync(db, orderId, ct: TestContext.Current.CancellationToken);

        var again = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Reviewing.Steps(db).CreateAsync(new CreateReviewRequest(orderId)))));

        Assert.Equal(StepOutcomes.Applied, first.Outcome);
        Assert.All(again, r => Assert.Equal((StepOutcomes.AlreadyApplied, first.ReviewId, 1, first.DeadlineAtUtc), (r.Outcome, r.ReviewId, r.Round, r.DeadlineAtUtc)));
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.ReviewTask WHERE OrderId = @o", ("@o", orderId)));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal("AwaitingApproval", s.Status);
        Assert.Equal("3:Validating>AwaitingApproval:ReviewRequired", s.History[^1]);
        Assert.Equal(3, s.History.Count);
        // The deadline is SQL time plus the configured duration.
        Assert.InRange((first.DeadlineAtUtc - await ServerNowAsync()).TotalSeconds, 250, 301);
    }

    [Fact]
    public async Task Decision_Approve_CommitsReviewDecisionAuditAndSignalTogether()
    {
        var (orderId, review) = await ReviewOrderAsync();
        var decision = Reviewing.Decision(comment: "looks fine");

        using var response = await Reviewing.DecideAsync(operators, review.ReviewId, decision, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DecisionAccepted(review.ReviewId, decision.DecisionId, "Approved", false), await response.Content.ReadFromJsonAsync<DecisionAccepted>(TestContext.Current.CancellationToken));
        var row = await Reviewing.ReviewAsync(db, review.ReviewId);
        Assert.Equal(("Approved", decision.DecisionId), ((string)row["Status"]!, (Guid)row["DecisionId"]!));
        var audit = (await Db.RowsAsync(db, "SELECT Outcome, OperatorId, Comment, OrderId FROM orders.ReviewDecision WHERE DecisionId = @d", ("@d", decision.DecisionId))).Single();
        Assert.Equal(("Approved", "operator-1", "looks fine", orderId), ((string)audit["Outcome"]!, (string)audit["OperatorId"]!, (string)audit["Comment"]!, (Guid)audit["OrderId"]!));
        var signal = Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        Assert.Equal(($"ReviewSignal_{review.ReviewId:N}", decision.DecisionId, "Pending"), ((string)signal["EventName"]!, (Guid)signal["CausedBy"]!, (string)signal["State"]!));
        // The decision does not change the order: the workflow applies it after reading it.
        Assert.Equal("AwaitingApproval", (await Fulfilling.SnapshotAsync(db, orderId)).Status);
    }

    [Fact]
    public async Task Decision_ContractResponses()
    {
        var (orderId, review) = await ReviewOrderAsync();
        var decision = Reviewing.Decision();
        using var accepted = await Reviewing.DecideAsync(operators, review.ReviewId, decision, TestContext.Current.CancellationToken);

        using var replay = await Reviewing.DecideAsync(operators, review.ReviewId, decision, TestContext.Current.CancellationToken);
        using var reused = await Reviewing.DecideAsync(operators, review.ReviewId, decision with { Outcome = DecisionOutcomes.Reject }, TestContext.Current.CancellationToken);
        using var other = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(DecisionOutcomes.Reject), TestContext.Current.CancellationToken);
        using var unknown = await Reviewing.DecideAsync(operators, Guid.NewGuid(), Reviewing.Decision(), TestContext.Current.CancellationToken);
        using var notOperator = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(op: "someone"), TestContext.Current.CancellationToken);
        using var customer = await Reviewing.DecideAsync(client, review.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);
        using var invalid = await Reviewing.DecideRawAsync(operators, review.ReviewId, "{\"decisionId\":\"" + Guid.NewGuid() + "\",\"operatorId\":\"operator-1\",\"outcome\":\"approve\",\"extra\":1}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<DecisionAccepted>(TestContext.Current.CancellationToken))!.Replayed);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "decision-id-reused"), (reused.StatusCode, await Reviewing.CodeAsync(reused)));
        Assert.Equal((HttpStatusCode.Conflict, "review-already-decided"), (other.StatusCode, await Reviewing.CodeAsync(other)));
        Assert.Equal((HttpStatusCode.NotFound, "review-not-found"), (unknown.StatusCode, await Reviewing.CodeAsync(unknown)));
        Assert.Equal((HttpStatusCode.Forbidden, "operator-not-authorized"), (notOperator.StatusCode, await Reviewing.CodeAsync(notOperator)));
        Assert.Equal(HttpStatusCode.Unauthorized, customer.StatusCode);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-decision"), (invalid.StatusCode, await Reviewing.CodeAsync(invalid)));
        // Only the first request wrote anything.
        Assert.Equal(1, await Reviewing.DecisionsAsync(db, review.ReviewId));
        Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        Assert.Equal("Approved", (await Reviewing.ReviewAsync(db, review.ReviewId))["Status"]);
    }

    [Fact]
    public async Task Decision_ConcurrentConflictingOperators_ExactlyOneCommits()
    {
        var (orderId, review) = await ReviewOrderAsync();
        var decisions = Enumerable.Range(0, 20)
            .Select(i => Reviewing.Decision(i % 2 == 0 ? DecisionOutcomes.Approve : DecisionOutcomes.Reject, op: i % 3 == 0 ? "operator-2" : "operator-1")).ToList();

        var responses = await Task.WhenAll(decisions.Select(d => Task.Run(() => Reviewing.DecideAsync(operators, review.ReviewId, d))));

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var winner = await Assert.Single(winners).Content.ReadFromJsonAsync<DecisionAccepted>(TestContext.Current.CancellationToken);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await Reviewing.DecisionsAsync(db, review.ReviewId));
        Assert.Single(await Reviewing.SignalsAsync(db, orderId));
        var row = await Reviewing.ReviewAsync(db, review.ReviewId);
        Assert.Equal((winner!.Outcome, winner.DecisionId), ((string)row["Status"]!, (Guid)row["DecisionId"]!));
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task Decision_AtOrAfterTheSqlDeadline_IsRefusedWithoutWriting()
    {
        var (orderId, review) = await ReviewOrderAsync();
        // The deadline is exactly SQL "now": the decision's own comparison (SQL time < deadline) refuses it.
        await Db.ExecuteAsync(db, $"UPDATE orders.ReviewTask SET DeadlineAt = SYSUTCDATETIME() WHERE ReviewId = '{review.ReviewId}'");

        using var late = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.Conflict, "review-expired"), (late.StatusCode, await Reviewing.CodeAsync(late)));
        Assert.Equal("Pending", (await Reviewing.ReviewAsync(db, review.ReviewId))["Status"]);
        Assert.Equal(0, await Reviewing.DecisionsAsync(db, review.ReviewId));
        Assert.Empty(await Reviewing.SignalsAsync(db, orderId));
    }

    [Fact]
    public async Task Decision_JustBeforeTheSqlDeadline_IsAccepted()
    {
        var (_, review) = await ReviewOrderAsync();
        await Db.ExecuteAsync(db, $"UPDATE orders.ReviewTask SET DeadlineAt = DATEADD(second, 3, SYSUTCDATETIME()) WHERE ReviewId = '{review.ReviewId}'");

        using var inTime = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, inTime.StatusCode);
        // Approved before the deadline: expiry afterwards cannot undo it.
        await Reviewing.ExpireDeadlineAsync(db, review.ReviewId);
        var expiry = await Reviewing.Steps(db).ExpireOrEscalateAsync(new ExpireReviewRequest(await OrderOfAsync(review.ReviewId), review.ReviewId), TestContext.Current.CancellationToken);
        Assert.Equal(ExpiryOutcomes.AlreadyDecided, expiry.Outcome);
        Assert.Equal("Approved", (await Reviewing.ReviewAsync(db, review.ReviewId))["Status"]);
    }

    [Theory]
    [InlineData(DecisionCheckpoints.AfterReviewUpdate)]
    [InlineData(DecisionCheckpoints.AfterDecisionInsert)]
    [InlineData(DecisionCheckpoints.AfterSignalInsert)]
    public async Task Decision_FailureInsideItsTransaction_RollsBackEverythingAndRetrySucceeds(string point)
    {
        var (orderId, review) = await ReviewOrderAsync();
        var decision = Reviewing.Decision();

        await Assert.ThrowsAsync<InjectedFault>(() => new ReviewDecisions(db, FailAt(point)).DecideAsync(review.ReviewId, decision, TestContext.Current.CancellationToken));

        Assert.Equal("Pending", (await Reviewing.ReviewAsync(db, review.ReviewId))["Status"]);
        Assert.Equal(0, await Reviewing.DecisionsAsync(db, review.ReviewId));
        Assert.Empty(await Reviewing.SignalsAsync(db, orderId));
        var retried = await new ReviewDecisions(db, StepCheckpoints.None).DecideAsync(review.ReviewId, decision, TestContext.Current.CancellationToken);
        Assert.Equal(DecisionOutcomeKind.Accepted, retried.Kind);
        Assert.Single(await Reviewing.SignalsAsync(db, orderId));
    }

    [Fact]
    public async Task Decision_CrashAfterCommitBeforeDispatch_KeepsTheDecisionAndItsPendingSignal()
    {
        var (orderId, review) = await ReviewOrderAsync();
        var decision = Reviewing.Decision();

        await Assert.ThrowsAsync<InjectedFault>(() => new ReviewDecisions(db, FailAt(DecisionCheckpoints.AfterCommit)).DecideAsync(review.ReviewId, decision, TestContext.Current.CancellationToken));
        var retry = await new ReviewDecisions(db, StepCheckpoints.None).DecideAsync(review.ReviewId, decision, TestContext.Current.CancellationToken);

        Assert.Equal(DecisionOutcomeKind.Replayed, retry.Kind);
        Assert.Equal("Approved", (await Reviewing.ReviewAsync(db, review.ReviewId))["Status"]);
        Assert.Equal("Pending", Assert.Single(await Reviewing.SignalsAsync(db, orderId))["State"]);
    }

    [Fact]
    public async Task Decision_AuditRowsAreImmutable()
    {
        var (_, review) = await ReviewOrderAsync();
        using var accepted = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<SqlException>(() => Db.ExecuteAsync(db, $"UPDATE orders.ReviewDecision SET Outcome = 'Rejected' WHERE ReviewId = '{review.ReviewId}'"));

        Assert.Contains("immutable", error.Message, StringComparison.Ordinal);
        Assert.Equal("Approved", await Db.ScalarAsync<string>(db, "SELECT Outcome FROM orders.ReviewDecision WHERE ReviewId = @r", ("@r", review.ReviewId)));
    }

    [Fact]
    public async Task ApprovedReview_IsResolvedThenFulfilledWithACoherentHistory()
    {
        var (orderId, review) = await ReviewOrderAsync();
        using var accepted = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);

        var resolution = await Reviewing.Steps(db).ResolveAsync(new ResolveReviewRequest(orderId, review.ReviewId, ReviewWakes.Signal), TestContext.Current.CancellationToken);
        var steps = Fulfilling.Steps(db);
        var begun = await steps.BeginFulfillmentAsync(new BeginFulfillmentRequest(orderId), TestContext.Current.CancellationToken);
        await steps.ApplyStubStepAsync(new StubStepRequest(orderId, StubSteps.Fulfil, "line-1"), TestContext.Current.CancellationToken);
        var done = await Fulfilling.CompleteAsync(steps, orderId, TestContext.Current.CancellationToken);

        Assert.Equal((ReviewStates.Approved, false), (resolution.State, resolution.OrderTerminal));
        Assert.Equal((StepOutcomes.Applied, StepOutcomes.Applied), (begun.Outcome, done.Outcome));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal(
            ["1:>Submitted:Accepted", "2:Submitted>Validating:ValidationStarted", "3:Validating>AwaitingApproval:ReviewRequired",
             "4:AwaitingApproval>Reserving:ReviewApproved", "5:Reserving>Completed:StubFulfillmentCompleted"],
            s.History);
        Assert.Equal("Closed", s.RequestState);
        Assert.Equal(["Signal:Approved"], (await Db.RowsAsync(db, "SELECT Wake, SeenStatus FROM orders.ReviewWake WHERE ReviewId = @r ORDER BY Id", ("@r", review.ReviewId)))
            .Select(r => $"{r["Wake"]}:{r["SeenStatus"]}"));
    }

    [Fact]
    public async Task BeginFulfillment_ReviewNotApproved_IsRefused()
    {
        var (orderId, _) = await ReviewOrderAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Fulfilling.Steps(db).BeginFulfillmentAsync(new BeginFulfillmentRequest(orderId), TestContext.Current.CancellationToken));

        Assert.Equal("AwaitingApproval", (await Fulfilling.SnapshotAsync(db, orderId)).Status);
    }

    [Fact]
    public async Task RejectedReview_RejectsTheOrderAndClosesItsStartRequest()
    {
        var (orderId, review) = await ReviewOrderAsync();
        using var rejected = await Reviewing.DecideAsync(operators, review.ReviewId, Reviewing.Decision(DecisionOutcomes.Reject), TestContext.Current.CancellationToken);

        var first = await Reviewing.Steps(db).ResolveAsync(new ResolveReviewRequest(orderId, review.ReviewId, ReviewWakes.Signal), TestContext.Current.CancellationToken);
        var again = await Reviewing.Steps(db).ResolveAsync(new ResolveReviewRequest(orderId, review.ReviewId, ReviewWakes.Timer), TestContext.Current.CancellationToken);

        Assert.Equal((ReviewStates.Rejected, "Rejected", true), (first.State, first.OrderStatus, first.OrderTerminal));
        Assert.Equal((ReviewStates.Rejected, "Rejected"), (again.State, again.OrderStatus));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal("4:AwaitingApproval>Rejected:ReviewRejected", s.History[^1]);
        Assert.Equal(4, s.History.Count);
        Assert.Equal("Closed", s.RequestState);
    }

    [Fact]
    public async Task Expiry_OnlyBySqlTime_ThenNextRound_ThenFinalRejection()
    {
        var (orderId, round1) = await ReviewOrderAsync();
        var steps = Reviewing.Steps(db);

        var early = await steps.ExpireOrEscalateAsync(new ExpireReviewRequest(orderId, round1.ReviewId), TestContext.Current.CancellationToken);
        await Reviewing.ExpireDeadlineAsync(db, round1.ReviewId);
        var next = await steps.ExpireOrEscalateAsync(new ExpireReviewRequest(orderId, round1.ReviewId), TestContext.Current.CancellationToken);
        var repeated = await steps.ExpireOrEscalateAsync(new ExpireReviewRequest(orderId, round1.ReviewId), TestContext.Current.CancellationToken);
        using var lateForRound1 = await Reviewing.DecideAsync(operators, round1.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);

        Assert.Equal(ExpiryOutcomes.NotDue, early.Outcome);
        Assert.Equal(ExpiryOutcomes.NextRound, next.Outcome);
        Assert.Equal((2, "Pending"), (next.NextRound!.Round, (string)(await Reviewing.ReviewAsync(db, next.NextRound.ReviewId))["Status"]!));
        Assert.Equal(next.NextRound.ReviewId, repeated.NextRound!.ReviewId);
        Assert.Equal("Expired", (await Reviewing.ReviewAsync(db, round1.ReviewId))["Status"]);
        Assert.Equal((HttpStatusCode.Conflict, "review-superseded"), (lateForRound1.StatusCode, await Reviewing.CodeAsync(lateForRound1)));
        Assert.Equal("AwaitingApproval", (await Fulfilling.SnapshotAsync(db, orderId)).Status);

        await Reviewing.ExpireDeadlineAsync(db, next.NextRound.ReviewId);
        var final = await steps.ExpireOrEscalateAsync(new ExpireReviewRequest(orderId, next.NextRound.ReviewId), TestContext.Current.CancellationToken);
        var finalAgain = await steps.ExpireOrEscalateAsync(new ExpireReviewRequest(orderId, next.NextRound.ReviewId), TestContext.Current.CancellationToken);
        using var lateForRound2 = await Reviewing.DecideAsync(operators, next.NextRound.ReviewId, Reviewing.Decision(), TestContext.Current.CancellationToken);

        Assert.Equal((ExpiryOutcomes.FinalExpiry, "Rejected"), (final.Outcome, final.OrderStatus));
        Assert.Equal(ExpiryOutcomes.FinalExpiry, finalAgain.Outcome);
        Assert.Equal((HttpStatusCode.Conflict, "review-expired"), (lateForRound2.StatusCode, await Reviewing.CodeAsync(lateForRound2)));
        var s = await Fulfilling.SnapshotAsync(db, orderId);
        Assert.Equal("4:AwaitingApproval>Rejected:ReviewExpired", s.History[^1]);
        Assert.Equal(4, s.History.Count);
        Assert.Equal("Closed", s.RequestState);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.ReviewDecision d JOIN orders.ReviewTask r ON r.ReviewId = d.ReviewId WHERE r.OrderId = @o", ("@o", orderId)));
    }

    [Fact]
    public async Task Resolve_PendingReview_ReportsWhetherSqlTimeReachedTheDeadline()
    {
        var (orderId, review) = await ReviewOrderAsync();
        var steps = Reviewing.Steps(db);

        var before = await steps.ResolveAsync(new ResolveReviewRequest(orderId, review.ReviewId, ReviewWakes.Timer), TestContext.Current.CancellationToken);
        await Reviewing.ExpireDeadlineAsync(db, review.ReviewId);
        var after = await steps.ResolveAsync(new ResolveReviewRequest(orderId, review.ReviewId, ReviewWakes.Timer), TestContext.Current.CancellationToken);

        Assert.Equal((ReviewStates.Pending, false), (before.State, before.DeadlinePassed));
        Assert.Equal((ReviewStates.Pending, true), (after.State, after.DeadlinePassed));
        Assert.Equal("Pending", (await Reviewing.ReviewAsync(db, review.ReviewId))["Status"]);
    }

    private async Task<(Guid OrderId, ReviewRound Review)> ReviewOrderAsync()
    {
        var orderId = await AcceptAsync();
        return (orderId, await Reviewing.OpenAsync(db, orderId, ct: Ct));
    }

    private Task<Guid> OrderOfAsync(Guid reviewId) => Db.ScalarAsync<Guid>(db, "SELECT OrderId FROM orders.ReviewTask WHERE ReviewId = @r", ("@r", reviewId));

    private Task<DateTime> ServerNowAsync() => Db.ScalarAsync<DateTime>(db, "SELECT SYSUTCDATETIME()");

    private async Task<Guid> AcceptAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("review"), ("SKU-1", 1)));
        return (await Orders.AcceptedAsync(response)).OrderId;
    }

    private static StepCheckpoint FailAt(string point) => (name, _, _) => name == point ? throw new InjectedFault(point) : Task.CompletedTask;

    private sealed class InjectedFault(string point) : Exception($"injected fault at {point}");
}
