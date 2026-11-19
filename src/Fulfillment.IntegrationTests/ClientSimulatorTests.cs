using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Runs;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Reviews;
using Fulfillment.IntegrationTests.Infrastructure;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The client simulator against the real order API and SQL Server, with two API hosts on one
/// database as replicas. Assertions read the database: a replay or a retry never adds an order.
/// </summary>
public sealed class ClientSimulatorTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly RetryPolicy Fast = new(4, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50));
    private static readonly Credentials Keys = new(OrderApiHost.CustomerApiKey, OrderApiHost.OperatorApiKey, "integration-admin-key");
    private string db = "";
    private OrderApiHost replica1 = null!;
    private OrderApiHost replica2 = null!;
    private readonly List<HttpClient> clients = [];

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("client");
        replica1 = new OrderApiHost(db);
        replica2 = new OrderApiHost(db);
    }

    public async ValueTask DisposeAsync()
    {
        clients.ForEach(c => c.Dispose());
        await replica1.DisposeAsync();
        await replica2.DisposeAsync();
    }

    [Fact]
    public async Task Load_NewRunReplayAndReusedRunId_ReproduceTheRequestsWithoutNewOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = new WorkloadSpec(RunId("s23"), 23, 12, ReviewRate: 0.25);
        var api = Api();

        var first = await LoadRun.RunAsync(api, new LoadOptions(spec, RunModes.New, Concurrency: 4), ct);
        var recorded = await RecordAsync(first);
        var replay = await LoadRun.RunAsync(api, new LoadOptions(recorded.Spec, RunModes.Replay, recorded, Concurrency: 4, Distribution: Distribution.Seeded), ct);
        var reused = await LoadRun.RunAsync(api, new LoadOptions(spec, RunModes.New, Concurrency: 4), ct);

        var created = Records(first);
        Assert.Equal((ExitCodes.Ok, 12), (first.ExitCode, created.Count(r => r.Acceptance == Acceptances.Created)));
        Assert.Equal(["replica-1", "replica-2"], created.Select(r => r.FirstEndpoint).Distinct().Order());
        // Replay: same digest, each request answered with its original order (here from the other replica too).
        Assert.Equal((ExitCodes.Ok, first.WorkloadDigest), (replay.ExitCode, replay.WorkloadDigest));
        Assert.Equal(created.Select(r => (r.IdempotencyKey, r.PayloadSha256, r.OrderId)), Records(replay).Select(r => (r.IdempotencyKey, r.PayloadSha256, r.OrderId)));
        Assert.All(Records(replay), r => Assert.Equal(Acceptances.Replayed, r.Acceptance));
        // A new run under a used run id is refused as such, and creates nothing.
        Assert.Equal(ExitCodes.RunIdentityMismatch, reused.ExitCode);
        Assert.All(Records(reused), r => Assert.Equal(Acceptances.Replayed, r.Acceptance));
        Assert.Equal((12, 12, 12), await CountsAsync(spec.RunId));
        Assert.DoesNotContain(OrderApiHost.CustomerApiKey, ResultJson.Render(first, Keys.Redactor), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_ResponseLostAfterCommit_RetriesTheSameRequestAndGetsTheSameOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = new WorkloadSpec(RunId("lost"), 5, 6);
        var lossy = new LoseFirstResponse();
        var http = replica1.CreateDefaultClient(lossy);
        clients.Add(http);
        var api = new ApiClient([new ApiEndpoint("replica-1", new Uri("http://localhost/"), http)], Keys, Fast);

        var result = await LoadRun.RunAsync(api, new LoadOptions(spec, RunModes.New, Concurrency: 3), ct);

        // The first answer of every POST was lost after the server committed: each retry is a replay of it.
        var records = Records(result);
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.All(records, r =>
        {
            Assert.Equal((Acceptances.Replayed, 2), (r.Acceptance, r.Attempts.Count));
            Assert.Equal(("transport", AttemptClasses.Ambiguous), (r.Attempts[0].Code, r.Attempts[0].Class));
            Assert.True(r.ResolvedAfterAmbiguous);
        });
        Assert.Equal(6, lossy.Lost);
        Assert.Equal((6, 6, 6), await CountsAsync(spec.RunId));
    }

    [Fact]
    public async Task Load_UnresolvedRequests_AreReportedAndAReplayResolvesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = new WorkloadSpec(RunId("unres"), 8, 3);
        var lossy = new LoseFirstResponse(int.MaxValue);
        var http = replica1.CreateDefaultClient(lossy);
        clients.Add(http);
        var blind = new ApiClient([new ApiEndpoint("replica-1", new Uri("http://localhost/"), http)], Keys, Fast with { MaxAttempts = 2 });

        var lost = await LoadRun.RunAsync(blind, new LoadOptions(spec, RunModes.New), ct);
        var replay = await LoadRun.RunAsync(Api(), new LoadOptions(spec, RunModes.Replay), ct);

        Assert.Equal(ExitCodes.Unresolved, lost.ExitCode);
        Assert.All(Records(lost), r => Assert.Equal(Acceptances.Unresolved, r.Acceptance));
        Assert.Equal(ExitCodes.Ok, replay.ExitCode);
        Assert.Equal((3, 3, 3), await CountsAsync(spec.RunId));
    }

    [Fact]
    public async Task Duplicates_ConcurrentCopiesOnBothReplicas_OneOrderPerKeyAndTheConflictRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = new WorkloadSpec(RunId("dup"), 3, 3);

        var result = await DuplicatesRun.RunAsync(Api(), new DuplicatesOptions(spec, Copies: 6), ct);

        var cases = Assert.IsType<List<DuplicateCase>>(result.Items);
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.All(cases, c =>
        {
            Assert.Equal((1, 1), (c.DistinctOrderIds.Count, c.Created));
            Assert.Equal(5, c.Copies.Count(r => r.Acceptance == Acceptances.Replayed));
            Assert.Equal(["replica-1", "replica-2"], c.Copies.Select(r => r.FirstEndpoint).Distinct().Order());
            Assert.Equal((Acceptances.Conflict, 422), (c.ConflictingRequest!.Acceptance, c.ConflictingRequest.HttpStatus));
        });
        Assert.Equal((3, 3, 3), await CountsAsync(spec.RunId));
    }

    [Fact]
    public async Task Conflicts_RacedBodiesOnBothReplicas_OneAcceptedTheRestRefusedOneOrderWithTheWinnersLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = new WorkloadSpec(RunId("conf"), 6, 3);

        var result = await ConflictsRun.RunAsync(Api(), new ConflictsOptions(spec, Variants: 5), ct);

        var cases = Assert.IsType<List<ConflictCase>>(result.Items);
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        foreach (var c in cases)
        {
            var winner = c.Variants[c.WinnerVariant!.Value];
            Assert.Equal(4, c.Variants.Count(v => v.Race.Acceptance == Acceptances.Conflict && v.Repeat!.Acceptance == Acceptances.Conflict));
            Assert.Equal((Acceptances.Replayed, c.OrderId), (winner.Repeat!.Acceptance, winner.Repeat.OrderId));
            // Every answer names its replica (X-Served-By); in-process hosts share one machine name.
            Assert.All(c.Variants, v => Assert.False(string.IsNullOrEmpty(v.Race.ServedBy)));
            // The order holds the winner's body: its customer and lines.
            var lines = await Db.RowsAsync(db, "SELECT Sku, Quantity FROM orders.OrderLine WHERE OrderId = @o ORDER BY LineNumber", ("@o", c.OrderId!.Value));
            Assert.Equal(winner.Lines.Select(l => (l.Sku, l.Quantity)), lines.Select(r => ((string)r["Sku"]!, (int)r["Quantity"]!)));
            Assert.Equal(winner.CustomerId, await Db.ScalarAsync<string>(db, "SELECT CustomerId FROM orders.[Order] WHERE OrderId = @o", ("@o", c.OrderId!.Value)));
        }

        Assert.Equal(3, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] LIKE @p", ("@p", spec.RunId + "-%")));
        Assert.Equal(3, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE @p", ("@p", spec.RunId + "-%")));
    }

    [Fact]
    public async Task Approve_PendingReview_DecidesOnceARerunIsAReplayAndAnotherDecisionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var api = Api();
        var intent = WorkloadGenerator.Generate(new WorkloadSpec(RunId("appr"), 1, 1, ReviewRate: 1))[0];
        var single = await SingleRun.RunAsync(api, new SingleOptions(intent, null, RunModes.New), ct);
        var orderId = Records(single)[0].OrderId!.Value;
        var review = await Reviewing.OpenAsync(db, orderId, ct: ct);

        var approved = await ApproveRun.RunAsync(api, new ApproveOptions([orderId], [], OperatorId: "operator-2"), ct);
        var again = await ApproveRun.RunAsync(api, new ApproveOptions([orderId], [], OperatorId: "operator-2"), ct);
        var direct = await ApproveRun.RunAsync(api, new ApproveOptions([], [review.ReviewId], OperatorId: "operator-2"), ct);
        var rejected = await ApproveRun.RunAsync(api, new ApproveOptions([orderId], [], DecisionOutcomes.Reject, "operator-1"), ct);

        Assert.True(intent.RequiresReview);
        var decision = Assert.Single(Assert.IsType<List<DecisionRecord>>(approved.Items));
        Assert.Equal((ExitCodes.Ok, DecisionResults.Accepted, review.ReviewId), (approved.ExitCode, decision.Result, decision.ReviewId));
        // The same command again (by order or by review) sends the same decision id: a replay.
        Assert.Equal((ExitCodes.Ok, DecisionResults.Replayed), (again.ExitCode, Assert.Single(Assert.IsType<List<DecisionRecord>>(again.Items)).Result));
        Assert.Equal(DecisionResults.Replayed, Assert.Single(Assert.IsType<List<DecisionRecord>>(direct.Items)).Result);
        // Another decision for the decided review is refused by the server.
        Assert.Equal((ExitCodes.ExpectationFailed, DecisionResults.AlreadyDecided), (rejected.ExitCode, Assert.Single(Assert.IsType<List<DecisionRecord>>(rejected.Items)).Result));
        Assert.Equal(1, await Reviewing.DecisionsAsync(db, review.ReviewId));
        Assert.Equal(decision.DecisionId, (Guid)(await Reviewing.ReviewAsync(db, review.ReviewId))["DecisionId"]!);
        Assert.DoesNotContain(OrderApiHost.OperatorApiKey, ResultJson.Render(approved, Keys.Redactor), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_ShowsTheOrderAndItsReviewsAndSkipsDiagnosticsWithoutTheAdminKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var noAdmin = new ApiClient(Api().Endpoints, new Credentials(OrderApiHost.CustomerApiKey, OrderApiHost.OperatorApiKey, null), Fast);
        var intent = WorkloadGenerator.Generate(new WorkloadSpec(RunId("insp"), 2, 1, ReviewRate: 1))[0];
        var orderId = Records(await SingleRun.RunAsync(noAdmin, new SingleOptions(intent, null), ct))[0].OrderId!.Value;
        await Reviewing.OpenAsync(db, orderId, ct: ct);

        var result = await InspectRun.RunAsync(noAdmin, [orderId, Guid.NewGuid()], null, ct);

        var records = Assert.IsType<List<InspectRecord>>(result.Items);
        Assert.Equal(ExitCodes.ExpectationFailed, result.ExitCode);
        Assert.Equal(("ok", "AwaitingApproval"), (records[0].Order.State, records[0].Order.Data!.Value.GetProperty("status").GetString()));
        Assert.Equal(("ok", 1), (records[0].Reviews.State, records[0].Reviews.Data!.Value.GetArrayLength()));
        Assert.Equal(("skipped", "ADMIN_API_KEY is not set"), (records[0].Execution.State, records[0].Execution.Reason));
        Assert.Equal("not-found", records[1].Order.State);
    }

    private ApiClient Api()
    {
        var one = replica1.CreateClient();
        var two = replica2.CreateClient();
        clients.AddRange([one, two]);
        return new ApiClient([new ApiEndpoint("replica-1", new Uri("http://localhost/"), one), new ApiEndpoint("replica-2", new Uri("http://localhost/"), two)], Keys, Fast);
    }

    private static string RunId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private static SubmissionRecord[] Records(RunResult result) => Assert.IsType<SubmissionRecord[]>(result.Items);

    private static async Task<RecordedRun> RecordAsync(RunResult result)
    {
        var path = Path.Combine(Path.GetTempPath(), $"client-sim-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, ResultJson.Render(result, Keys.Redactor));
            return RecordedRun.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Orders, idempotency keys, and start requests of a run (by its customer and key prefix).</summary>
    private async Task<(int Orders, int Keys, int StartRequests)> CountsAsync(string runId) =>
        (await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.[Order] WHERE CustomerId LIKE @p", ("@p", runId + "-%")),
         await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.IdempotencyKey WHERE [Key] LIKE @p", ("@p", runId + "-%")),
         await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM orders.WorkflowStartRequest r JOIN orders.[Order] o ON o.OrderId = r.OrderId WHERE o.CustomerId LIKE @p", ("@p", runId + "-%")));

    /// <summary>Lets the server process each POST, then drops its answer (a response lost after commit) for the first N of each key.</summary>
    private sealed class LoseFirstResponse(int losesPerKey = 1) : DelegatingHandler
    {
        private readonly Dictionary<string, int> seen = [];
        private int lost;

        public int Lost => lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && request.Headers.TryGetValues(OrderRoutes.IdempotencyKeyHeader, out var keys))
            {
                var key = keys.Single();
                bool drop;
                lock (seen)
                {
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                    drop = seen[key] <= losesPerKey;
                }

                if (drop)
                {
                    response.Dispose();
                    Interlocked.Increment(ref lost);
                    throw new HttpRequestException("simulated: response lost after the server committed");
                }
            }

            return response;
        }
    }
}
