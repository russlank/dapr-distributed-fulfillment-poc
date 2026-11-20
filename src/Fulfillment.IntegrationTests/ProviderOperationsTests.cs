extern alias provider;
using Fulfillment.Contracts.Payments;
using Fulfillment.IntegrationTests.Infrastructure;
using provider::Fulfillment.PaymentProviderSimulator.Provider;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The payment provider simulator's operations against SQL Server: idempotency by operation key, the business
/// rules, and compensation by operation key with durable prohibitions (a late original never commits after its
/// compensation). Assertions read the provider's rows.
/// </summary>
public sealed class ProviderOperationsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private ProviderOperations ops = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateProviderDbAsync("provider");
        ops = new ProviderOperations(db, new ProviderOptions { AuthorizationValidity = TimeSpan.FromMinutes(10), DeclineFromMinor = 1_000_000 });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Key(string order, string step) => $"{order}:{step}";

    private static ProviderOperationRequest Authorize(string key, long amount = 5_000) => new(key, ProviderKinds.Authorize, amount, "EUR", null);

    private static ProviderOperationRequest Capture(string key, string auth, long amount = 5_000) => new(key, ProviderKinds.Capture, amount, "EUR", auth);

    private static ProviderOperationRequest Void(string key, string auth) => new(key, ProviderKinds.Void, 0, "EUR", auth);

    private static ProviderOperationRequest Refund(string key, string capture, long amount = 5_000) => new(key, ProviderKinds.Refund, amount, "EUR", capture);

    private async Task<ProviderOperationResponse> RunAsync(ProviderOperationRequest request)
    {
        var outcome = await ops.ExecuteAsync(request, Ct);
        Assert.Equal(ProviderOutcomeKind.Ok, outcome.Kind);
        return outcome.Response!;
    }

    private Task<int> CountAsync(string where, params (string, object)[] p) =>
        Db.ScalarAsync<int>(db, $"SELECT COUNT(*) FROM provider.Operation WHERE {where}", p);

    [Fact]
    public async Task Authorize_RepeatedKey_ReturnsTheStoredResultAndCountsReplays()
    {
        var key = Key("o1", "authorize:1");
        var first = await RunAsync(Authorize(key));
        var second = await RunAsync(Authorize(key));

        Assert.Equal((ProviderStatuses.Committed, ProviderResults.Authorized, false), (first.Status, first.Result, first.Replayed));
        Assert.Equal((ProviderResults.Authorized, true, first.CommittedAtUtc), (second.Result, second.Replayed, second.CommittedAtUtc));
        Assert.Equal(1, await Db.ScalarAsync<int>(db, "SELECT Replays FROM provider.Operation WHERE OperationKey = @k", ("@k", key)));
        Assert.NotNull(first.AuthorizationExpiresAtUtc);
    }

    [Fact]
    public async Task Authorize_SameKeyOtherPayload_IsAConflictAndChangesNothing()
    {
        var key = Key("o2", "authorize:1");
        await RunAsync(Authorize(key, 5_000));

        var conflict = await ops.ExecuteAsync(Authorize(key, 6_000), Ct);

        Assert.Equal(ProviderOutcomeKind.PayloadConflict, conflict.Kind);
        Assert.Equal(5_000, await Db.ScalarAsync<long>(db, "SELECT AmountMinor FROM provider.Operation WHERE OperationKey = @k", ("@k", key)));
    }

    [Fact]
    public async Task Authorize_TwentyConcurrentRequestsOfOneKey_OneOperationAndOneAnswer()
    {
        var key = Key("o3", "authorize:1");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => RunAsync(Authorize(key)), Ct)));

        Assert.Single(results.Select(r => (r.Result, r.CommittedAtUtc)).Distinct());
        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(1, await CountAsync("OperationKey = @k", ("@k", key)));
    }

    [Fact]
    public async Task Authorize_FromTheDeclineAmount_IsDeclinedAndCannotBeCaptured()
    {
        var auth = Key("o4", "authorize:1");
        Assert.Equal(ProviderResults.Declined, (await RunAsync(Authorize(auth, 1_000_000))).Result);

        var capture = await RunAsync(Capture(Key("o4", "capture:1"), auth, 1_000_000));

        Assert.Equal((ProviderResults.Rejected, ProviderResultCodes.AuthorizationNotUsable), (capture.Result, capture.ResultCode));
    }

    [Fact]
    public async Task Void_BeforeTheAuthorizationExists_ProhibitsItsKey_LateAuthorizeNeverCommits()
    {
        var auth = Key("o5", "authorize:1");
        var voided = await RunAsync(Void(Key("o5", "void:1"), auth));

        var late = await RunAsync(Authorize(auth));
        var status = await ops.QueryAsync(auth, Ct);

        Assert.Equal((ProviderResults.NothingToVoid, ProviderResultCodes.ReferenceNeverCommitted), (voided.Result, voided.ResultCode));
        Assert.Equal((ProviderStatuses.Prohibited, Key("o5", "void:1")), (late.Status, late.ProhibitedBy));
        Assert.Equal(ProviderStatuses.Prohibited, status.Status);
        Assert.Equal(0, await CountAsync("OperationKey = @k", ("@k", auth)));
    }

    [Fact]
    public async Task Void_RacingItsAuthorization_NeverLeavesALiveAuthorizationBehind()
    {
        for (var i = 0; i < 30; i++)
        {
            var auth = Key($"r{i}", "authorize:1");
            var voidKey = Key($"r{i}", "void:1");
            var results = await Task.WhenAll(Task.Run(() => RunAsync(Authorize(auth)), Ct), Task.Run(() => RunAsync(Void(voidKey, auth)), Ct));
            var (a, v) = (results[0], results[1]);

            // Either the authorization committed first and was voided, or the void came first and prohibited it.
            var authorizedThenVoided = a.Result == ProviderResults.Authorized && v.Result == ProviderResults.Voided;
            var prohibited = a.Status == ProviderStatuses.Prohibited && v.Result == ProviderResults.NothingToVoid;
            Assert.True(authorizedThenVoided || prohibited, $"iteration {i}: authorize {a.Status}/{a.Result}, void {v.Result}");
        }

        Assert.Equal(0, await Db.ScalarAsync<int>(db,
            """
            SELECT COUNT(*) FROM provider.Operation a WHERE a.Kind = 'Authorize' AND a.Result = 'Authorized'
              AND NOT EXISTS (SELECT 1 FROM provider.Operation v WHERE v.Reference = a.OperationKey AND v.Kind = 'Void' AND v.Result = 'Voided')
            """));
    }

    [Fact]
    public async Task Capture_AfterTheAuthorizationWasVoided_IsRejected()
    {
        var auth = Key("o6", "authorize:1");
        await RunAsync(Authorize(auth));
        await RunAsync(Void(Key("o6", "void:1"), auth));

        var capture = await RunAsync(Capture(Key("o6", "capture:1"), auth));

        Assert.Equal((ProviderResults.Rejected, ProviderResultCodes.AuthorizationVoided), (capture.Result, capture.ResultCode));
    }

    [Fact]
    public async Task Void_AfterTheCapture_IsRejected_TheCaptureStands()
    {
        var auth = Key("o7", "authorize:1");
        await RunAsync(Authorize(auth));
        await RunAsync(Capture(Key("o7", "capture:1"), auth));

        var voided = await RunAsync(Void(Key("o7", "void:1"), auth));

        Assert.Equal((ProviderResults.Rejected, ProviderResultCodes.AlreadyCaptured), (voided.Result, voided.ResultCode));
        Assert.Equal(1, await CountAsync("Reference = @a AND Result = 'Captured'", ("@a", auth)));
    }

    [Fact]
    public async Task Capture_RacingAVoid_ExactlyOneOfThemTakesEffect()
    {
        for (var i = 0; i < 30; i++)
        {
            var auth = Key($"cv{i}", "authorize:1");
            await RunAsync(Authorize(auth));
            await Task.WhenAll(Task.Run(() => RunAsync(Capture(Key($"cv{i}", "capture:1"), auth)), Ct), Task.Run(() => RunAsync(Void(Key($"cv{i}", "void:1"), auth)), Ct));

            Assert.Equal(1, await CountAsync("Reference = @a AND Result IN ('Captured', 'Voided')", ("@a", auth)));
        }
    }

    [Fact]
    public async Task Refund_BeforeTheCaptureExists_ProhibitsIt_LateCaptureNeverCommits()
    {
        var auth = Key("o8", "authorize:1");
        var capture = Key("o8", "capture:1");
        await RunAsync(Authorize(auth));

        var refund = await RunAsync(Refund(Key("o8", "refund:1"), capture));
        var late = await RunAsync(Capture(capture, auth));

        Assert.Equal((ProviderResults.NothingToRefund, ProviderResultCodes.ReferenceNeverCommitted), (refund.Result, refund.ResultCode));
        Assert.Equal(ProviderStatuses.Prohibited, late.Status);
        Assert.Equal(0, await CountAsync("Kind = 'Capture' AND Reference = @a", ("@a", auth)));
    }

    [Fact]
    public async Task Refund_RacingItsCapture_NeverLeavesAnUnrefundedCaptureBehind()
    {
        for (var i = 0; i < 30; i++)
        {
            var auth = Key($"rc{i}", "authorize:1");
            var capture = Key($"rc{i}", "capture:1");
            await RunAsync(Authorize(auth));
            var results = await Task.WhenAll(Task.Run(() => RunAsync(Capture(capture, auth)), Ct), Task.Run(() => RunAsync(Refund(Key($"rc{i}", "refund:1"), capture)), Ct));
            var (c, r) = (results[0], results[1]);

            var capturedThenRefunded = c.Result == ProviderResults.Captured && r.Result == ProviderResults.Refunded;
            var prohibited = c.Status == ProviderStatuses.Prohibited && r.Result == ProviderResults.NothingToRefund;
            Assert.True(capturedThenRefunded || prohibited, $"iteration {i}: capture {c.Status}/{c.Result}, refund {r.Result}");
        }
    }

    [Fact]
    public async Task Refund_FullAmountOnce_PartialRejected_SecondKeyHasNothingLeft()
    {
        var auth = Key("o9", "authorize:1");
        var capture = Key("o9", "capture:1");
        await RunAsync(Authorize(auth));
        await RunAsync(Capture(capture, auth));

        var partial = await RunAsync(Refund(Key("o9", "refund:p"), capture, 1_000));
        var full = await RunAsync(Refund(Key("o9", "refund:1"), capture));
        var second = await RunAsync(Refund(Key("o9", "refund:2"), capture));

        Assert.Equal((ProviderResults.Rejected, ProviderResultCodes.AmountMismatch), (partial.Result, partial.ResultCode));
        Assert.Equal(ProviderResults.Refunded, full.Result);
        Assert.Equal(ProviderResults.NothingToRefund, second.Result);
        Assert.Equal(1, await CountAsync("Reference = @c AND Result = 'Refunded'", ("@c", capture)));
    }

    [Fact]
    public async Task Capture_OfAnExpiredAuthorization_IsRejected()
    {
        var auth = Key("o10", "authorize:1");
        await Db.ExecuteAsync(db, "INSERT provider.Fault (Label, Match, Mode, Param, RemainingUses) VALUES ('t', @k, 'ExpireAuthorization', 0, 1)", ("@k", auth));
        await RunAsync(Authorize(auth));

        var capture = await RunAsync(Capture(Key("o10", "capture:1"), auth));

        Assert.Equal((ProviderResults.Rejected, ProviderResultCodes.AuthorizationExpired), (capture.Result, capture.ResultCode));
        Assert.Equal("ExpireAuthorization", await Db.ScalarAsync<string>(db, "SELECT FaultApplied FROM provider.Operation WHERE OperationKey = @k", ("@k", auth)));
    }

    [Fact]
    public async Task Faults_DeclineAndRejectBeforeCommit_AreAppliedOnceAndRecorded()
    {
        var declined = Key("o11", "authorize:1");
        var rejected = Key("o12", "authorize:1");
        await Db.ExecuteAsync(db, "INSERT provider.Fault (Label, Match, Mode, RemainingUses) VALUES ('t', @a, 'Decline', 1), ('t', @b, 'RejectBeforeCommit', 1)", ("@a", declined), ("@b", rejected));

        var first = await RunAsync(Authorize(declined));
        var refusedOnce = await ops.ExecuteAsync(Authorize(rejected), Ct);
        var retried = await RunAsync(Authorize(rejected));

        Assert.Equal(ProviderResults.Declined, first.Result);
        Assert.Equal(ProviderOutcomeKind.RejectedBeforeCommit, refusedOnce.Kind);
        Assert.Equal(ProviderResults.Authorized, retried.Result);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM provider.Fault WHERE RemainingUses > 0"));
    }

    [Fact]
    public async Task StatusQuery_AnswersCommittedNotFoundAndUnknown_AndLogsEveryAnswer()
    {
        var auth = Key("o13", "authorize:1");
        await Db.ExecuteAsync(db, "INSERT provider.Fault (Label, Match, Mode, RemainingUses) VALUES ('t', @k, 'StatusUnknown', 2)", ("@k", auth));
        await RunAsync(Authorize(auth));

        var answers = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            answers.Add((await ops.QueryAsync(auth, Ct)).Status);
        }

        var missing = await ops.QueryAsync(Key("o13", "capture:1"), Ct);

        Assert.Equal([ProviderStatuses.Unknown, ProviderStatuses.Unknown, ProviderStatuses.Committed], answers);
        Assert.Equal(ProviderStatuses.NotFound, missing.Status);
        Assert.Equal(4, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM provider.StatusQuery WHERE OperationKey LIKE 'o13:%'"));
    }

    [Fact]
    public async Task Schema_RejectsASecondEffectOnOneReference()
    {
        var auth = Key("o14", "authorize:1");
        await RunAsync(Authorize(auth));
        await RunAsync(Capture(Key("o14", "capture:1"), auth));

        var error = await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => Db.ExecuteAsync(db,
            "INSERT provider.Operation (OperationKey, Kind, PayloadHash, AmountMinor, Currency, Reference, Result, Replays) VALUES ('o14:capture:2', 'Capture', 0x00, 1, 'EUR', @a, 'Captured', 0)",
            ("@a", auth)));

        Assert.Contains("UX_Operation_EffectPerReference", error.Message, StringComparison.Ordinal);
    }
}
