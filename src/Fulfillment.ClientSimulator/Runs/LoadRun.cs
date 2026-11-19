using System.Diagnostics;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Workload;

namespace Fulfillment.ClientSimulator.Runs;

public static class RunModes
{
    /// <summary>A new logical run: every request must create a new order.</summary>
    public const string New = "new";

    /// <summary>The same logical run again: every request must return the original order.</summary>
    public const string Replay = "replay";

    /// <summary>No expectation about earlier requests (single orders).</summary>
    public const string Any = "any";

    /// <summary>Only the generated requests, nothing sent.</summary>
    public const string DryRun = "dry-run";
}

/// <param name="Original">For a replay: the earlier result (workload, digest, order ids).</param>
/// <param name="Concurrency">Requests in flight at most.</param>
/// <param name="Rate">Started orders per second at most (null: as fast as the concurrency allows).</param>
public sealed record LoadOptions(
    WorkloadSpec Spec,
    string Mode,
    RecordedRun? Original = null,
    int Concurrency = 4,
    double? Rate = null,
    Distribution Distribution = Distribution.RoundRobin,
    int? PinnedReplica = null,
    FollowOptions? Follow = null);

/// <summary>
/// The load command: a seeded workload sent with bounded concurrency across the replicas. A new run
/// must create every order; a replay (from an earlier result) must regenerate the same requests
/// (equal workload digest) and get the original order back for each.
/// </summary>
public static class LoadRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, LoadOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var intents = WorkloadGenerator.Generate(options.Spec);
        var digest = WorkloadGenerator.Digest(intents);
        var endpoints = api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList();

        if (options.Mode == RunModes.DryRun)
        {
            var planned = intents.Select(i => new
            {
                i.Index,
                i.IdempotencyKey,
                i.CustomerId,
                i.Lines,
                i.PayloadSha256,
                i.RequiresReview,
                FirstEndpoint = api.Endpoints[ReplicaPlan.FirstEndpoint(options.Distribution, options.Spec.Seed, i.Index, api.Endpoints.Count, options.PinnedReplica)].Name,
            }).ToList();
            return new RunResult("load", RunModes.DryRun, options.Spec.RunId, options.Spec, digest, endpoints, started, DateTimeOffset.UtcNow,
                new { intents = intents.Count, requiringReview = intents.Count(i => i.RequiresReview) }, planned, [], ExitCodes.Ok);
        }

        if (options.Mode == RunModes.Replay && options.Original is { } original && original.Digest != digest)
        {
            return new RunResult("load", RunModes.Replay, options.Spec.RunId, options.Spec, digest, endpoints, started, DateTimeOffset.UtcNow,
                new { intents = 0 }, Array.Empty<object>(),
                [$"the regenerated workload differs from the original (digest {digest} vs {original.Digest}): nothing sent"], ExitCodes.RunIdentityMismatch);
        }

        var records = new SubmissionRecord[intents.Count];
        await Parallel.ForEachAsync(intents, new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = ct }, async (intent, token) =>
        {
            if (options.Rate is { } rate)
            {
                var due = TimeSpan.FromSeconds((intent.Index - 1) / rate) - watch.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, token);
                }
            }

            var first = ReplicaPlan.FirstEndpoint(options.Distribution, options.Spec.Seed, intent.Index, api.Endpoints.Count, options.PinnedReplica);
            var record = await OrderOperations.SubmitAsync(api, intent, first, options.PinnedReplica is not null, token);
            record = Judge(record, options);
            if (options.Follow is { } follow && record.OrderId is { } orderId)
            {
                var followed = await OrderOperations.FollowAsync(api, orderId, first, follow, token);
                record = record with
                {
                    Follow = followed,
                    Problem = record.Problem ?? (followed.Reached ? null : $"order {orderId}: {followed.Error}"),
                    ExitCode = ExitCodes.Combine([record.ExitCode, followed.Reached ? ExitCodes.Ok : ExitCodes.ExpectationFailed]),
                };
            }

            records[intent.Index - 1] = record;
        });

        var problems = records.Where(r => r.Problem is not null).Select(r => $"#{r.Index} {r.IdempotencyKey}: {r.Problem}").ToList();
        var summary = Summarize(records, watch.Elapsed);
        return new RunResult("load", options.Mode, options.Spec.RunId, options.Spec, digest, endpoints, started, DateTimeOffset.UtcNow,
            summary, records, problems, ExitCodes.Combine(records.Select(r => r.ExitCode)));
    }

    /// <summary>Applies the run mode's expectation to one answer.</summary>
    public static SubmissionRecord Judge(SubmissionRecord record, LoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(options);
        Guid? recorded = options.Original?.OrderIds.TryGetValue(record.Index, out var id) == true ? id : null;
        var (problem, code) = (record.Acceptance, options.Mode) switch
        {
            (Acceptances.NotSent, _) => ("not sent: " + record.Attempts[^1].Error, ExitCodes.Usage),
            (Acceptances.Unresolved, _) => ($"no definite answer after {record.Attempts.Count} attempts: the order may or may not exist; replay the run to resolve it", ExitCodes.Unresolved),
            (Acceptances.Refused, _) => ($"refused: http {record.HttpStatus} {record.Code}", ExitCodes.ExpectationFailed),
            (Acceptances.Conflict, _) => ("the key was used before with a different request (run id reused with another workload?)", ExitCodes.RunIdentityMismatch),
            // A replay after an ambiguous attempt of this run is this run's own earlier request taking effect.
            (Acceptances.Replayed, RunModes.New) when !record.ResolvedAfterAmbiguous => ($"already accepted before as order {record.OrderId}: run id '{options.Spec.RunId}' was used; replay it with --replay-of, or start a new run", ExitCodes.RunIdentityMismatch),
            (Acceptances.Created, RunModes.Replay) when recorded is not null => ($"created a new order {record.OrderId} where the original run recorded {recorded}", ExitCodes.RunIdentityMismatch),
            (Acceptances.Replayed, RunModes.Replay) when recorded is not null && recorded != record.OrderId => ($"replayed order {record.OrderId}, the original run recorded {recorded}", ExitCodes.RunIdentityMismatch),
            _ => ((string?)null, ExitCodes.Ok),
        };
        return record with { Problem = problem, ExitCode = code };
    }

    public static object Summarize(IReadOnlyCollection<SubmissionRecord> records, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(records);
        var followed = records.Where(r => r.Follow is not null).ToList();
        return new
        {
            intents = records.Count,
            accepted = records.Count(r => r.Acceptance is Acceptances.Created or Acceptances.Replayed),
            created = records.Count(r => r.Acceptance == Acceptances.Created),
            replayed = records.Count(r => r.Acceptance == Acceptances.Replayed),
            conflicts = records.Count(r => r.Acceptance == Acceptances.Conflict),
            refused = records.Count(r => r.Acceptance == Acceptances.Refused),
            unresolved = records.Count(r => r.Acceptance == Acceptances.Unresolved),
            notSent = records.Count(r => r.Acceptance == Acceptances.NotSent),
            requiringReview = records.Count(r => r.RequiresReview),
            ambiguousAttempts = records.Sum(r => r.Attempts.Count(a => a.Class == AttemptClasses.Ambiguous)),
            resolvedAfterAmbiguous = records.Count(r => r.ResolvedAfterAmbiguous),
            byServer = records.Where(r => r.ServedBy is not null).GroupBy(r => r.ServedBy!)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            byEndpoint = records.GroupBy(r => r.Attempts.Count > 0 ? r.Attempts[^1].Endpoint : r.FirstEndpoint)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            followed = followed.Count,
            completed = followed.Count(r => r.Follow!.FinalStatus == "Completed"),
            finalStatuses = followed.GroupBy(r => r.Follow!.FinalStatus ?? "unknown")
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            notReached = followed.Count(r => !r.Follow!.Reached),
            elapsedSeconds = Math.Round(elapsed.TotalSeconds, 3),
        };
    }
}
