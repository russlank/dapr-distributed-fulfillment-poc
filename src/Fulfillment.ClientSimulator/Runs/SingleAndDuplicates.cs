using System.Diagnostics;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Workload;

namespace Fulfillment.ClientSimulator.Runs;

/// <param name="Intent">The order to submit (key, customer, lines).</param>
/// <param name="Expect">new (must create), replay (must return an earlier order), or any.</param>
public sealed record SingleOptions(OrderIntent Intent, string? RunId, string Expect = RunModes.Any, int? PinnedReplica = null, FollowOptions? Follow = null);

/// <summary>The single command: one order, optionally followed until it is terminal.</summary>
public static class SingleRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, SingleOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var started = DateTimeOffset.UtcNow;
        var first = options.PinnedReplica is { } pinned ? pinned - 1 : 0;
        var record = await OrderOperations.SubmitAsync(api, options.Intent, first, options.PinnedReplica is not null, ct);
        var (problem, code) = (record.Acceptance, options.Expect) switch
        {
            (Acceptances.NotSent, _) => ("not sent: " + record.Attempts[^1].Error, ExitCodes.Usage),
            (Acceptances.Unresolved, _) => ($"no definite answer after {record.Attempts.Count} attempts; resubmit with the same key to resolve it", ExitCodes.Unresolved),
            (Acceptances.Refused, _) => ($"refused: http {record.HttpStatus} {record.Code}", ExitCodes.ExpectationFailed),
            (Acceptances.Conflict, _) => ("the key was used before with a different request", ExitCodes.ExpectationFailed),
            (Acceptances.Replayed, RunModes.New) when !record.ResolvedAfterAmbiguous => ($"the key was accepted before (order {record.OrderId})", ExitCodes.RunIdentityMismatch),
            (Acceptances.Created, RunModes.Replay) => ($"no earlier order with this key: created {record.OrderId}", ExitCodes.RunIdentityMismatch),
            _ => ((string?)null, ExitCodes.Ok),
        };
        record = record with { Problem = problem, ExitCode = code };
        if (options.Follow is { } follow && record.OrderId is { } orderId)
        {
            var followed = await OrderOperations.FollowAsync(api, orderId, first, follow, ct);
            record = record with
            {
                Follow = followed,
                Problem = record.Problem ?? (followed.Reached ? null : followed.Error),
                ExitCode = ExitCodes.Combine([record.ExitCode, followed.Reached ? ExitCodes.Ok : ExitCodes.ExpectationFailed]),
            };
        }

        return new RunResult("single", options.Expect, options.RunId, null, null, api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(),
            started, DateTimeOffset.UtcNow, new { acceptance = record.Acceptance, orderId = record.OrderId, finalStatus = record.Follow?.FinalStatus },
            new[] { record }, record.Problem is null ? [] : [record.Problem], record.ExitCode);
    }
}

public sealed record DuplicatesOptions(WorkloadSpec Spec, int Copies = 4, bool Conflict = true, int? PinnedReplica = null);

public sealed record DuplicateCase(
    int Index,
    string IdempotencyKey,
    IReadOnlyList<SubmissionRecord> Copies,
    SubmissionRecord? ConflictingRequest,
    IReadOnlyList<Guid> DistinctOrderIds,
    int Created,
    string? Problem,
    int ExitCode);

/// <summary>
/// The duplicates command: each intent is sent several times at once, spread over the replicas, then
/// once with the same key and a different body. Expected: one order per key (at most one 201, every
/// other copy 200 with the same order id) and 422 idempotency-key-reused for the different body.
/// </summary>
public static class DuplicatesRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, DuplicatesOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var intents = WorkloadGenerator.Generate(options.Spec);
        var pinned = options.PinnedReplica is not null;
        var cases = new List<DuplicateCase>();
        foreach (var intent in intents)
        {
            var copies = await Task.WhenAll(Enumerable.Range(0, options.Copies).Select(copy =>
                OrderOperations.SubmitAsync(api, intent, pinned ? options.PinnedReplica!.Value - 1 : intent.Index - 1 + copy, pinned, ct)));
            SubmissionRecord? conflicting = null;
            if (options.Conflict)
            {
                var changed = WorkloadGenerator.Create(intent.Index, intent.IdempotencyKey, intent.CustomerId + "-x", intent.Lines, intent.RequiresReview);
                conflicting = await OrderOperations.SubmitAsync(api, changed, pinned ? options.PinnedReplica!.Value - 1 : intent.Index, pinned, ct);
            }

            cases.Add(Judge(intent, copies, conflicting));
        }

        var problems = cases.Where(c => c.Problem is not null).Select(c => $"#{c.Index} {c.IdempotencyKey}: {c.Problem}").ToList();
        var summary = new
        {
            keys = cases.Count,
            copiesPerKey = options.Copies,
            keysWithOneOrder = cases.Count(c => c.DistinctOrderIds.Count == 1),
            created = cases.Sum(c => c.Created),
            replayed = cases.Sum(c => c.Copies.Count(r => r.Acceptance == Acceptances.Replayed)),
            conflictsRefused = cases.Count(c => c.ConflictingRequest?.Acceptance == Acceptances.Conflict),
            ambiguousAttempts = cases.Sum(c => c.Copies.Sum(r => r.Attempts.Count(a => a.Class == AttemptClasses.Ambiguous))),
            elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
        };
        return new RunResult("duplicates", RunModes.New, options.Spec.RunId, options.Spec, WorkloadGenerator.Digest(intents),
            api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(), started, DateTimeOffset.UtcNow, summary, cases, problems,
            ExitCodes.Combine(cases.Select(c => c.ExitCode)));
    }

    public static DuplicateCase Judge(OrderIntent intent, IReadOnlyList<SubmissionRecord> copies, SubmissionRecord? conflicting)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(copies);
        var ids = copies.Where(c => c.OrderId is not null).Select(c => c.OrderId!.Value).Distinct().ToList();
        var created = copies.Count(c => c.Acceptance == Acceptances.Created);
        var problems = new List<string>();
        var codes = new List<int>();
        if (copies.Any(c => c.Acceptance == Acceptances.NotSent))
        {
            problems.Add("not sent (customer key missing)");
            codes.Add(ExitCodes.Usage);
        }

        if (copies.Any(c => c.Acceptance == Acceptances.Unresolved) || conflicting?.Acceptance == Acceptances.Unresolved)
        {
            problems.Add("some copies have no definite answer");
            codes.Add(ExitCodes.Unresolved);
        }

        if (ids.Count > 1 || created > 1)
        {
            problems.Add($"one key, {ids.Count} orders ({created} created): duplicate business effect");
            codes.Add(ExitCodes.ExpectationFailed);
        }

        if (copies.Any(c => c.Acceptance is Acceptances.Refused or Acceptances.Conflict))
        {
            problems.Add("an identical copy was refused");
            codes.Add(ExitCodes.ExpectationFailed);
        }

        if (conflicting is not null && conflicting.Acceptance is not (Acceptances.Conflict or Acceptances.Unresolved))
        {
            problems.Add($"the different body under the same key was answered {conflicting.Acceptance} (http {conflicting.HttpStatus}), not 422 idempotency-key-reused");
            codes.Add(ExitCodes.ExpectationFailed);
        }

        return new DuplicateCase(intent.Index, intent.IdempotencyKey, copies, conflicting, ids, created,
            problems.Count == 0 ? null : string.Join("; ", problems), ExitCodes.Combine(codes));
    }
}
