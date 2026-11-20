using System.Net;
using System.Text;
using Fulfillment.ClientSimulator;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Runs;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.ComputeWorker.Checks;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Orders;
using Xunit;

namespace Fulfillment.UnitTests;

/// <summary>S23 at unit level: the same run id and seed reproduce the same logical requests and keys.</summary>
public class WorkloadGeneratorTests
{
    private static readonly WorkloadSpec Golden = new("golden-run", 23, 5, ReviewRate: 0.5);

    [Fact]
    public void DeterministicRandom_SeedZero_MatchesTheSplitMix64Reference()
    {
        Assert.Equal(0xE220A8397B1DCDAFUL, new DeterministicRandom(0).NextUInt64());
    }

    [Fact]
    public void Generate_SameSpecTwice_YieldsIdenticalKeysBodiesAndDigest()
    {
        var first = WorkloadGenerator.Generate(Golden);
        var second = WorkloadGenerator.Generate(Golden with { });

        Assert.Equal(first.Select(i => (i.IdempotencyKey, i.Body, i.PayloadSha256)), second.Select(i => (i.IdempotencyKey, i.Body, i.PayloadSha256)));
        Assert.Equal(WorkloadGenerator.Digest(first), WorkloadGenerator.Digest(second));
    }

    [Fact]
    public void Generate_GoldenSpec_KeepsItsDigestAcrossBuilds()
    {
        // A change here means the same seed no longer reproduces earlier runs: replays of recorded
        // results would be refused. Change the generator only deliberately, and say so in the journal.
        Assert.Equal("b24b16a9ca2583670cce1641d07a9cde14c8f046ba403d4abfdcf6f4743666a6", WorkloadGenerator.Digest(WorkloadGenerator.Generate(Golden)));
        Assert.Equal("golden-run-o00001", WorkloadGenerator.Generate(Golden)[0].IdempotencyKey);
    }

    [Fact]
    public void Generate_OtherSeed_KeepsTheKeysAndChangesThePayloads()
    {
        var a = WorkloadGenerator.Generate(Golden with { ReviewRate = 0 });
        var b = WorkloadGenerator.Generate(Golden with { ReviewRate = 0, Seed = 24 });

        Assert.Equal(a.Select(i => i.IdempotencyKey), b.Select(i => i.IdempotencyKey));
        Assert.NotEqual(a.Select(i => i.Body), b.Select(i => i.Body));
    }

    [Fact]
    public void Generate_OtherRunId_GivesNewKeysAndCustomers()
    {
        var a = WorkloadGenerator.Generate(Golden);
        var b = WorkloadGenerator.Generate(Golden with { RunId = "golden-run-2" });

        Assert.Empty(a.Select(i => i.IdempotencyKey).Intersect(b.Select(i => i.IdempotencyKey)));
        Assert.All(b, i => Assert.StartsWith("golden-run-2-c", i.CustomerId, StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_FixedSku_EveryOrderOneLineOfItAndValid()
    {
        var intents = WorkloadGenerator.Generate(new WorkloadSpec("contention-run", 3, 50, FixedSku: "SKU-C1", FixedQuantity: 2));

        Assert.All(intents, i =>
        {
            Assert.Equal([new SubmitOrderLine("SKU-C1", 2)], i.Lines);
            Assert.Empty(OrderRequests.Validate(OrderRequests.TryParse(Encoding.UTF8.GetBytes(i.Body), out _)!));
        });
        Assert.Equal(50, intents.Select(i => i.IdempotencyKey).Distinct().Count());
        Assert.Contains("sku", Assert.Single(new WorkloadSpec("contention-run", 3, 5, FixedSku: "bad sku").Problems()), StringComparison.Ordinal);
    }

    [Fact]
    public void Intent_DoesNotDependOnTheWorkloadSize()
    {
        var small = WorkloadGenerator.Generate(Golden with { Orders = 7 });
        var large = WorkloadGenerator.Generate(Golden with { Orders = 100 });

        Assert.Equal(small.Select(i => i.Body), large.Take(7).Select(i => i.Body));
    }

    [Fact]
    public void Generate_ManyOrders_AreAllValidForTheOrderApiAndUseDistinctSkus()
    {
        var intents = WorkloadGenerator.Generate(new WorkloadSpec("validity-run", 7, 500, ReviewRate: 0.2));

        Assert.All(intents, intent =>
        {
            var request = OrderRequests.TryParse(Encoding.UTF8.GetBytes(intent.Body), out var errorPath);
            Assert.True(request is not null, errorPath);
            Assert.Empty(OrderRequests.Validate(request!));
            Assert.True(OrderRequests.IsValidIdempotencyKey(intent.IdempotencyKey));
            Assert.Equal(intent.Lines.Count, intent.Lines.Select(l => l.Sku).Distinct(StringComparer.Ordinal).Count());
        });
        Assert.InRange(intents.Count(i => i.RequiresReview), 60, 140);
    }

    [Fact]
    public void StubPrices_EqualTheComputeWorkersPriceList()
    {
        foreach (var sku in Enumerable.Range(1, 300).SelectMany(n => new[] { $"SKU-{n}", $"SKU-R{n}" }))
        {
            Assert.Equal(StubPriceList.UnitPriceMinor(sku), StubPrices.UnitPriceMinor(sku));
        }

        var review = StubPrices.ReviewSku(StubPrices.DefaultReviewThresholdMinor);
        Assert.True(StubRiskRule.ReviewRequired(StubPriceList.UnitPriceMinor(review) * StubPrices.ReviewQuantity, StubPrices.DefaultReviewThresholdMinor));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("s23-20261119-a1", true)]
    [InlineData("ab", false)]
    [InlineData("Upper-case", false)]
    [InlineData("-leading", false)]
    [InlineData("a-run-id-that-is-much-longer-than-forty-chars", false)]
    public void RunIds_Validate(string runId, bool valid)
    {
        Assert.Equal(valid, RunIds.IsValid(runId));
    }

    [Fact]
    public void RunIds_New_AreValidAndDistinct()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 20).Select(_ => RunIds.New(now)).ToList();

        Assert.All(ids, id => Assert.True(RunIds.IsValid(id), id));
        Assert.True(ids.Distinct().Count() > 1);
    }

    [Fact]
    public void Spec_OutOfRange_ReportsEveryProblem()
    {
        var problems = new WorkloadSpec("x", 1, 0, Clients: 0, MaxLines: 30, ReviewRate: 2).Problems();

        Assert.Equal(5, problems.Count);
    }
}

public class ClientSimulatorPolicyTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(201, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(422, false)]
    [InlineData(408, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    public void IsAmbiguous_OnlyForAnswersThatLeaveTheOutcomeOpen(int status, bool ambiguous)
    {
        Assert.Equal(ambiguous, AttemptClasses.IsAmbiguous(status));
    }

    [Fact]
    public void RetryPolicy_DelayGrowsAndIsCapped()
    {
        var policy = RetryPolicy.Default;

        Assert.Equal(TimeSpan.Zero, policy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayBefore(30));
    }

    [Fact]
    public void ReplicaPlan_RoundRobinPinnedAndSeeded()
    {
        Assert.Equal([0, 1, 0, 1], Enumerable.Range(1, 4).Select(i => ReplicaPlan.FirstEndpoint(Distribution.RoundRobin, 1, i, 2, null)));
        Assert.All(Enumerable.Range(1, 4), i => Assert.Equal(1, ReplicaPlan.FirstEndpoint(Distribution.RoundRobin, 1, i, 2, 2)));
        var seeded = Enumerable.Range(1, 200).Select(i => ReplicaPlan.FirstEndpoint(Distribution.Seeded, 9, i, 3, null)).ToList();
        Assert.Equal(seeded, Enumerable.Range(1, 200).Select(i => ReplicaPlan.FirstEndpoint(Distribution.Seeded, 9, i, 3, null)));
        Assert.Equal([0, 1, 2], seeded.Distinct().Order());
    }

    [Fact]
    public void ExitCodes_Combine_ByPriority()
    {
        Assert.Equal(ExitCodes.Ok, ExitCodes.Combine([]));
        Assert.Equal(ExitCodes.ExpectationFailed, ExitCodes.Combine([0, 1, 0]));
        Assert.Equal(ExitCodes.Unresolved, ExitCodes.Combine([1, 4]));
        Assert.Equal(ExitCodes.RunIdentityMismatch, ExitCodes.Combine([4, 3, 1]));
        Assert.Equal(ExitCodes.Usage, ExitCodes.Combine([3, 2]));
    }

    [Fact]
    public void DecisionId_IsStableVersion8AndDependsOnTheDecision()
    {
        var review = Guid.Parse("6a1c7c1e-55b9-4cf0-9f43-5a4c0f0b6d11");
        var id = ApproveRun.DecisionId(review, "approve", "operator-1", "default");

        Assert.Equal(id, ApproveRun.DecisionId(review, "approve", "operator-1", "default"));
        Assert.Equal('8', id.ToString()[14]);
        Assert.NotEqual(id, ApproveRun.DecisionId(review, "reject", "operator-1", "default"));
        Assert.NotEqual(id, ApproveRun.DecisionId(review, "approve", "operator-2", "default"));
        Assert.NotEqual(id, ApproveRun.DecisionId(review, "approve", "operator-1", "other"));
    }

    [Fact]
    public void Redactor_MasksEverySecretInTheRenderedResult()
    {
        var credentials = new Credentials("customer-secret-1", "operator-secret-2", "admin-secret-3");
        var result = new RunResult("load", RunModes.New, "run-1", null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new { note = "customer-secret-1 and admin-secret-3" }, Array.Empty<object>(), ["operator-secret-2 leaked"], 1);

        var json = ResultJson.Render(result, credentials.Redactor);

        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Contains(Redactor.Mask, json, StringComparison.Ordinal);
    }

    private static readonly OrderIntent Intent = WorkloadGenerator.Generate(new WorkloadSpec("judge-run", 1, 1))[0];

    private static SubmissionRecord Record(string acceptance, Guid? orderId = null) =>
        new(1, Intent.IdempotencyKey, Intent.CustomerId, Intent.Lines, Intent.PayloadSha256, false, "replica-1", acceptance, orderId, 200, null,
            [new AttemptRecord(1, "replica-1", 200, AttemptClasses.Answered, null, 1, null)]);

    [Fact]
    public void Judge_NewRun_RequiresCreation()
    {
        var spec = new WorkloadSpec("judge-run", 1, 1);

        Assert.Equal(ExitCodes.Ok, LoadRun.Judge(Record(Acceptances.Created, Guid.NewGuid()), new LoadOptions(spec, RunModes.New)).ExitCode);
        Assert.Equal(ExitCodes.RunIdentityMismatch, LoadRun.Judge(Record(Acceptances.Replayed, Guid.NewGuid()), new LoadOptions(spec, RunModes.New)).ExitCode);
        Assert.Equal(ExitCodes.RunIdentityMismatch, LoadRun.Judge(Record(Acceptances.Conflict), new LoadOptions(spec, RunModes.New)).ExitCode);
        Assert.Equal(ExitCodes.ExpectationFailed, LoadRun.Judge(Record(Acceptances.Refused), new LoadOptions(spec, RunModes.New)).ExitCode);
        Assert.Equal(ExitCodes.Unresolved, LoadRun.Judge(Record(Acceptances.Unresolved), new LoadOptions(spec, RunModes.New)).ExitCode);
    }

    [Fact]
    public void Judge_NewRun_ReplayAfterItsOwnAmbiguousAttempt_IsAccepted()
    {
        var spec = new WorkloadSpec("judge-run", 1, 1);
        var record = Record(Acceptances.Replayed, Guid.NewGuid()) with
        {
            Attempts =
            [
                new AttemptRecord(1, "replica-1", null, AttemptClasses.Ambiguous, "transport", 1, "reset"),
                new AttemptRecord(2, "replica-2", 200, AttemptClasses.Answered, null, 1, null),
            ],
        };

        Assert.Equal(ExitCodes.Ok, LoadRun.Judge(record, new LoadOptions(spec, RunModes.New)).ExitCode);
    }

    [Fact]
    public void Judge_Replay_RequiresTheRecordedOrder()
    {
        var spec = new WorkloadSpec("judge-run", 1, 1);
        var recorded = Guid.NewGuid();
        var original = new RecordedRun(spec, "digest", new Dictionary<int, Guid> { [1] = recorded });
        var replay = new LoadOptions(spec, RunModes.Replay, original);

        Assert.Equal(ExitCodes.Ok, LoadRun.Judge(Record(Acceptances.Replayed, recorded), replay).ExitCode);
        Assert.Equal(ExitCodes.RunIdentityMismatch, LoadRun.Judge(Record(Acceptances.Replayed, Guid.NewGuid()), replay).ExitCode);
        Assert.Equal(ExitCodes.RunIdentityMismatch, LoadRun.Judge(Record(Acceptances.Created, Guid.NewGuid()), replay).ExitCode);
    }

    [Fact]
    public void DuplicatesJudge_TwoOrdersForOneKey_FailsTheExpectation()
    {
        var copies = new[] { Record(Acceptances.Created, Guid.NewGuid()), Record(Acceptances.Created, Guid.NewGuid()) };

        var verdict = DuplicatesRun.Judge(Intent, copies, Record(Acceptances.Conflict));

        Assert.Equal(ExitCodes.ExpectationFailed, verdict.ExitCode);
        Assert.Equal(2, verdict.DistinctOrderIds.Count);
    }

    [Fact]
    public void ConflictVariants_AreDistinctValidBodiesUnderOneKey()
    {
        foreach (var intent in WorkloadGenerator.Generate(new WorkloadSpec("variant-run", 4, 30, ReviewRate: 0.3)))
        {
            var variants = Enumerable.Range(0, 8).Select(v => ConflictsRun.Variant(intent, v).Intent).ToList();

            Assert.Equal(intent.Body, variants[0].Body);
            Assert.Equal(8, variants.Select(v => v.PayloadSha256).Distinct().Count());
            Assert.All(variants, v =>
            {
                Assert.Equal(intent.IdempotencyKey, v.IdempotencyKey);
                Assert.Empty(OrderRequests.Validate(OrderRequests.TryParse(Encoding.UTF8.GetBytes(v.Body), out _)!));
            });
            // The server hashes the parsed request: every variant must also differ there.
            Assert.Equal(8, variants.Select(v => Convert.ToHexString(OrderRequests.Hash(OrderRequests.TryParse(Encoding.UTF8.GetBytes(v.Body), out _)!))).Distinct().Count());
        }
    }

    private static ConflictVariant Variant(int v, string race, string? repeat, Guid? orderId) =>
        new(v, "x", "h", Intent.Lines, Intent.CustomerId, Record(race, race is Acceptances.Created or Acceptances.Replayed ? orderId : null),
            repeat is null ? null : Record(repeat, repeat == Acceptances.Replayed ? orderId : null));

    [Fact]
    public void ConflictsJudge_OneWinnerRestRefused_Holds()
    {
        var id = Guid.NewGuid();
        var verdict = ConflictsRun.Judge(Intent, [Variant(0, Acceptances.Conflict, Acceptances.Conflict, id), Variant(1, Acceptances.Created, Acceptances.Replayed, id), Variant(2, Acceptances.Conflict, Acceptances.Conflict, id)]);

        Assert.Equal((ExitCodes.Ok, 1, id), (verdict.ExitCode, verdict.WinnerVariant!.Value, verdict.OrderId!.Value));
    }

    [Theory]
    [InlineData(Acceptances.Created, Acceptances.Created)]
    [InlineData(Acceptances.Conflict, Acceptances.Conflict)]
    [InlineData(Acceptances.Created, Acceptances.Replayed)]
    public void ConflictsJudge_NotExactlyOneAcceptedBody_Fails(string first, string second)
    {
        var verdict = ConflictsRun.Judge(Intent, [Variant(0, first, null, Guid.NewGuid()), Variant(1, second, null, Guid.NewGuid())]);

        Assert.Equal(ExitCodes.ExpectationFailed, verdict.ExitCode);
    }

    [Fact]
    public void ConflictsJudge_RefusedBodyAcceptedOnRepeat_Fails()
    {
        var id = Guid.NewGuid();
        var verdict = ConflictsRun.Judge(Intent, [Variant(0, Acceptances.Created, Acceptances.Replayed, id), Variant(1, Acceptances.Conflict, Acceptances.Replayed, id)]);

        Assert.Equal(ExitCodes.ExpectationFailed, verdict.ExitCode);
    }

    [Fact]
    public void ParseLines_AcceptsSkuQuantityAndReportsTheRest()
    {
        var problems = new List<string>();

        var lines = Cli.ParseLines(["SKU-1:2", "SKU-2:x", "nocolon"], problems);

        Assert.Equal([new SubmitOrderLine("SKU-1", 2)], lines);
        Assert.Equal(2, problems.Count);
    }
}

/// <summary>The HTTP layer against a scripted handler: retries are byte-identical and keyed.</summary>
public class ApiClientTests
{
    private static readonly RetryPolicy Fast = new(3, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));

    [Fact]
    public async Task Send_AmbiguousThenAnswered_ResendsTheSameKeyAndBodyToTheNextReplica()
    {
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Created());
        var api = Client(handler, 2, Fast);

        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, "/orders", "{\"a\":1}", "key-1", 0, false, TestContext.Current.CancellationToken);

        Assert.Equal(201, call.Status);
        Assert.Equal(["replica-1", "replica-2"], call.Attempts.Select(a => a.Endpoint));
        Assert.Equal([AttemptClasses.Ambiguous, AttemptClasses.Answered], call.Attempts.Select(a => a.Class));
        Assert.All(handler.Requests, r => Assert.Equal(("key-1", "{\"a\":1}", "customer-key"), (r.Key, r.Body, r.ApiKey)));
        Assert.Equal(["r1.test", "r2.test"], handler.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task Send_TransportErrorThenAnswered_IsAmbiguousThenAnswered()
    {
        using var handler = new ScriptedHandler(_ => throw new HttpRequestException("connection reset"), _ => Created());
        var api = Client(handler, 1, Fast);

        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, "/orders", "{}", "key-2", 0, true, TestContext.Current.CancellationToken);

        Assert.Equal(201, call.Status);
        Assert.Equal("transport", call.Attempts[0].Code);
        Assert.False(call.Unresolved);
    }

    [Fact]
    public async Task Send_Refusal_IsNotRetried()
    {
        using var handler = new ScriptedHandler(_ => Problem(HttpStatusCode.UnprocessableEntity, "idempotency-key-reused"), _ => Created());
        var api = Client(handler, 2, Fast);

        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, "/orders", "{}", "key-3", 0, false, TestContext.Current.CancellationToken);

        Assert.Equal((422, "idempotency-key-reused"), (call.Status, call.Code));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Send_NoAnswerEver_IsUnresolvedAfterEveryAttempt()
    {
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        var api = Client(handler, 2, Fast);

        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, "/orders", "{}", "key-4", 0, false, TestContext.Current.CancellationToken);

        Assert.True(call.Unresolved);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Send_Timeout_IsAmbiguous()
    {
        using var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Created();
        });
        var api = Client(handler, 1, Fast with { MaxAttempts = 1, RequestTimeout = TimeSpan.FromMilliseconds(50) });

        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, "/orders", "{}", "key-5", 0, true, TestContext.Current.CancellationToken);

        Assert.Equal(("timeout", true), (call.Code, call.Unresolved));
    }

    [Fact]
    public async Task Send_KeyMissing_SendsNothing()
    {
        using var handler = new ScriptedHandler(_ => Created());
        var api = new ApiClient([new ApiEndpoint("replica-1", new Uri("http://r1.test"), new HttpClient(handler, false))], new Credentials(null, null, null), Fast);

        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, "/orders", "{}", "key-6", 0, true, TestContext.Current.CancellationToken);

        Assert.Equal(("credential-missing", AttemptClasses.NotSent), (call.Code, call.Attempts[0].Class));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cli_LoadDryRun_PrintsTheWorkloadAndExitsZero()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Cli.Build(stdout, stderr).Parse(["load", "--run-id", "cli-run", "--seed", "23", "--orders", "3", "--dry-run"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("\"workloadDigest\"", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("cli-run-o00003", stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("load", "--orders", "0")]
    [InlineData("load", "--replay-of", "missing.json", "--seed", "3")]
    [InlineData("load", "--replay")]
    [InlineData("approve")]
    [InlineData("inspect")]
    [InlineData("cancel")]
    [InlineData("reviews")]
    [InlineData("escalate")]
    [InlineData("escalate", "--order", "6f0c1b8e-3f7a-4c39-9d55-2b1a7e5f0c11", "--reason", "")]
    [InlineData("resolve", "--action", "Acknowledge", "--evidence", "checked")]
    [InlineData("operator", "run", "--seed", "1")]
    [InlineData("operator", "run", "--seed", "1", "--order", "6f0c1b8e-3f7a-4c39-9d55-2b1a7e5f0c11", "--approve", "0")]
    [InlineData("single", "--url", "http://user:pw@host")]
    public async Task Cli_InvalidUse_ExitsTwo(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Cli.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.DoesNotContain("pw", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Help_ListsTheCommands()
    {
        var names = Cli.Build().Subcommands.Select(c => c.Name).ToList();

        Assert.Equal(["single", "duplicates", "conflicts", "contention", "load", "approve", "inspect", "cancel", "reviews", "escalate", "resolve", "operator", "identity", "probe"], names);
    }

    /// <summary>A tiny review server: rounds per order, decisions keyed by decision id, escalation opens round 2 once.</summary>
    private sealed class ReviewServer
    {
        private readonly Dictionary<Guid, List<(Guid Id, int Round, string Status, Guid? DecisionId)>> rounds = [];

        public ReviewServer(params Guid[] orders)
        {
            foreach (var order in orders)
            {
                rounds[order] = [(Guid.NewGuid(), 1, "Pending", null)];
            }
        }

        public int Decisions { get; private set; }

        public HttpResponseMessage Handle(HttpRequestMessage request, string? body)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                var order = Guid.Parse(request.RequestUri.Query.Split('=')[1]);
                var views = rounds[order].Select(r => new ReviewView(r.Id, order, "manual", r.Round, r.Status, DateTime.UtcNow, null, r.DecisionId));
                return Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(views, ResultJson.Options));
            }

            var reviewId = Guid.Parse(path.Split('/')[2]);
            var (orderId, list) = rounds.Single(kv => kv.Value.Any(r => r.Id == reviewId));
            var index = list.FindIndex(r => r.Id == reviewId);
            var round = list[index];
            if (path.EndsWith("/escalate", StringComparison.Ordinal))
            {
                var replay = round.Status == "Escalated";
                if (!replay)
                {
                    list[index] = round with { Status = "Escalated" };
                    list.Add((Guid.NewGuid(), 2, "Pending", null));
                }

                return Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new ReviewEscalated(reviewId, list[^1].Id, 2, replay), ResultJson.Options));
            }

            var decision = System.Text.Json.JsonSerializer.Deserialize<SubmitDecisionRequest>(body!, ResultJson.Options)!;
            if (round.Status == "Pending")
            {
                Decisions++;
                list[index] = round with { Status = decision.Outcome == "approve" ? "Approved" : "Rejected", DecisionId = decision.DecisionId };
                return Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new DecisionAccepted(reviewId, decision.DecisionId, decision.Outcome, false), ResultJson.Options));
            }

            return round.DecisionId == decision.DecisionId
                ? Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new DecisionAccepted(reviewId, decision.DecisionId, decision.Outcome, true), ResultJson.Options))
                : Problem(HttpStatusCode.Conflict, ReviewErrorCodes.ReviewAlreadyDecided);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task OperatorRun_SameSeedTwice_SecondRunIsAllReplays_NoSecondDecision()
    {
        var orders = Enumerable.Range(0, 12).Select(i => new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, (byte)i])).ToArray();
        var server = new ReviewServer(orders);
        using var handler = new ScriptedHandler(async (r, ct) => server.Handle(r, r.Content is null ? null : await r.Content.ReadAsStringAsync(ct)));
        var api = Client(handler, 1, Fast);
        var plan = OperatorRun.Plan(orders, 30, new OperatorWeights(1, 1, 1, 1), ["operator-1", "operator-2"]);

        var first = await OperatorRun.RunAsync(api, plan, 30, TimeSpan.Zero, null, TestContext.Current.CancellationToken);
        var decisions = server.Decisions;
        var second = await OperatorRun.RunAsync(api, plan, 30, TimeSpan.Zero, null, TestContext.Current.CancellationToken);

        var firstItems = (IReadOnlyList<OperatorRunRecord>)first.Items;
        var secondItems = (IReadOnlyList<OperatorRunRecord>)second.Items;
        Assert.Equal((ExitCodes.Ok, ExitCodes.Ok), (first.ExitCode, second.ExitCode));
        Assert.Equal(plan.Count(c => c.Action != OperatorRun.Ignore), decisions);
        Assert.Equal(decisions, server.Decisions);
        Assert.All(firstItems.Where(r => r.Action != OperatorRun.Ignore), r => Assert.Equal(DecisionResults.Accepted, r.Result));
        Assert.All(secondItems.Where(r => r.Action != OperatorRun.Ignore), r => Assert.Equal(DecisionResults.Replayed, r.Result));
        Assert.Contains(secondItems, r => r.Action == OperatorRun.Escalate && r.Calls.Any(c => c.Result == "replayed"));
        Assert.Equal(first.WorkloadDigest, second.WorkloadDigest);
    }

    [Fact]
    public async Task Cancel_MapsRecordedRepeatAndRefusal()
    {
        var calls = 0;
        using var handler = new ScriptedHandler(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"cancellation\":\"Requested\"}", Encoding.UTF8, "application/json") },
            2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"cancellation\":\"Requested\"}", Encoding.UTF8, "application/json") },
            _ => Problem(HttpStatusCode.Conflict, OrderErrorCodes.DispatchStarted),
        });
        var api = Client(handler, 1, Fast);

        var result = await CancelRun.RunAsync(api, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], null, TestContext.Current.CancellationToken);

        Assert.Equal(["recorded", "already-requested", OrderErrorCodes.DispatchStarted], ((IReadOnlyList<OperatorCallRecord>)result.Items).Select(r => r.Result));
        Assert.Equal(ExitCodes.ExpectationFailed, result.ExitCode);
        Assert.All(handler.Requests, r => Assert.Equal("customer-key", r.ApiKey));
    }

    [Fact]
    public async Task Resolve_SendsTheDerivedResolutionId_RepeatIsTheSameRequest()
    {
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"caseId\":\"00000000-0000-0000-0000-000000000001\",\"action\":\"Acknowledge\",\"path\":\"RecordOnly\",\"replayed\":false}", Encoding.UTF8, "application/json"),
        });
        var api = Client(handler, 1, Fast);
        var caseId = Guid.NewGuid();

        var result = await ResolveRun.RunAsync(api, [caseId, caseId], CaseActions.Acknowledge, "operator-1", "checked", "s", null, TestContext.Current.CancellationToken);

        Assert.Equal("resolved:RecordOnly", ((IReadOnlyList<OperatorCallRecord>)result.Items)[0].Result);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.Contains(ResolveRun.ResolutionId(caseId, CaseActions.Acknowledge, "operator-1", "s").ToString(), handler.Requests[0].Body!, StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Equal("operator-key", r.ApiKey));
    }

    private static ApiClient Client(ScriptedHandler handler, int replicas, RetryPolicy policy)
    {
        var http = new HttpClient(handler, false);
        var endpoints = Enumerable.Range(1, replicas).Select(i => new ApiEndpoint($"replica-{i}", new Uri($"http://r{i}.test"), http)).ToList();
        return new ApiClient(endpoints, new Credentials("customer-key", "operator-key", "admin-key"), policy);
    }

    private static HttpResponseMessage Created() =>
        new(HttpStatusCode.Created) { Content = new StringContent($"{{\"orderId\":\"{Guid.NewGuid()}\",\"status\":\"Submitted\",\"replayed\":false}}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent($"{{\"title\":\"x\",\"code\":\"{code}\"}}", Encoding.UTF8, "application/problem+json") };

    private sealed record SeenRequest(string Host, string? Key, string? ApiKey, string? Body);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) =>
            this.script = [.. script.Select(s => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((r, _) => Task.FromResult(s(r))))];

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) => script = [step];

        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SeenRequest(request.RequestUri!.Host,
                request.Headers.TryGetValues(OrderRoutes.IdempotencyKeyHeader, out var key) ? key.Single() : null,
                request.Headers.TryGetValues(OrderRoutes.ApiKeyHeader, out var apiKey) ? apiKey.Single() : null, body));
            return await script[Math.Min(Requests.Count, script.Length) - 1](request, cancellationToken);
        }
    }
}

/// <summary>S30 at unit level: the seeded scripted operator's plan depends only on the seed, weights, operators, and order ids.</summary>
public class OperatorPlanTests
{
    private static readonly Guid[] Orders = [.. Enumerable.Range(0, 40).Select(i => new Guid(i + 1, 7, 7, [1, 2, 3, 4, 5, 6, 7, (byte)i]))];

    private static readonly OperatorWeights Mixed = new(0.4, 0.2, 0.2, 0.2);

    [Fact]
    public void Plan_SameInputsTwice_SameChoicesAndDigest()
    {
        var a = OperatorRun.Plan(Orders, 30, Mixed, ["operator-1", "operator-2"]);
        var b = OperatorRun.Plan([.. Orders], 30, Mixed with { }, ["operator-1", "operator-2"]);

        Assert.Equal(a, b);
        Assert.Equal(OperatorRun.Digest(a), OperatorRun.Digest(b));
        Assert.Equal(4, a.Select(c => c.Action).Distinct().Count());
    }

    [Fact]
    public void Plan_ChoiceOfAnOrder_DoesNotDependOnTheOtherOrders()
    {
        var all = OperatorRun.Plan(Orders, 30, Mixed, ["operator-1", "operator-2"]);
        var reversedSubset = OperatorRun.Plan([.. Orders.Skip(10).Reverse()], 30, Mixed, ["operator-1", "operator-2"]);

        Assert.All(reversedSubset, c => Assert.Equal(all.Single(x => x.OrderId == c.OrderId), c));
    }

    [Fact]
    public void Plan_OtherSeed_ChangesTheChoices()
    {
        Assert.NotEqual(OperatorRun.Digest(OperatorRun.Plan(Orders, 30, Mixed, ["operator-1"])), OperatorRun.Digest(OperatorRun.Plan(Orders, 31, Mixed, ["operator-1"])));
    }

    [Fact]
    public void Plan_OnlyApproveWeight_ApprovesEverything()
    {
        Assert.All(OperatorRun.Plan(Orders, 5, new OperatorWeights(1, 0, 0, 0), ["operator-1"]), c => Assert.Equal(OperatorRun.Approve, c.Action));
    }

    [Fact]
    public void Plan_GoldenInputs_KeepTheirDigestAcrossBuilds()
    {
        // A change here means the same seed no longer reproduces earlier operator runs. Change the plan only deliberately.
        Assert.Equal("675b935152047a7c894771e3d10308940b0b4b5c69abda03c0b497c348478e17", OperatorRun.Digest(OperatorRun.Plan(Orders, 30, Mixed, ["operator-1", "operator-2"])));
    }

    [Fact]
    public void ResolutionId_IsStableAndDependsOnEveryPart()
    {
        var caseId = Guid.Parse("6f0c1b8e-3f7a-4c39-9d55-2b1a7e5f0c11");
        var id = ResolveRun.ResolutionId(caseId, "Acknowledge", "operator-1", "default");

        Assert.Equal(id, ResolveRun.ResolutionId(caseId, "Acknowledge", "operator-1", "default"));
        Assert.NotEqual(id, ResolveRun.ResolutionId(caseId, "ConfirmExternalFailure", "operator-1", "default"));
        Assert.NotEqual(id, ResolveRun.ResolutionId(caseId, "Acknowledge", "operator-2", "default"));
        Assert.NotEqual(id, ResolveRun.ResolutionId(caseId, "Acknowledge", "operator-1", "other"));
        Assert.Equal(8, id.ToByteArray(bigEndian: true)[6] >> 4);
    }
}
