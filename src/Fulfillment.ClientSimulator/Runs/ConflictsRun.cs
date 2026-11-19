using System.Diagnostics;
using System.Globalization;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.ClientSimulator.Runs;

/// <param name="Variants">Distinct bodies per key (variant 0 is the generated order).</param>
public sealed record ConflictsOptions(WorkloadSpec Spec, int Variants = 4, int? PinnedReplica = null, FollowOptions? Follow = null);

public sealed record ConflictVariant(int Variant, string Change, string PayloadSha256, IReadOnlyList<SubmitOrderLine> Lines, string CustomerId,
    SubmissionRecord Race, SubmissionRecord? Repeat);

public sealed record ConflictCase(int Index, string IdempotencyKey, IReadOnlyList<ConflictVariant> Variants, int? WinnerVariant, Guid? OrderId, string? Problem, int ExitCode)
{
    public IReadOnlyList<string> ServedBy => [.. Variants.SelectMany(v => new[] { v.Race, v.Repeat }).Where(r => r?.ServedBy is not null)
        .Select(r => r!.ServedBy!).Distinct().Order(StringComparer.Ordinal)];

    public FollowRecord? Follow { get; init; }
}

/// <summary>
/// The conflicts command: for each key, distinct bodies are sent at once over the replicas; exactly
/// one may be accepted and every other must be refused with 422 idempotency-key-reused. Then each
/// body is sent once more to another replica: the accepted one is replayed with the same order, the
/// others are refused again, whichever replica answers.
/// </summary>
public static class ConflictsRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, ConflictsOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var intents = WorkloadGenerator.Generate(options.Spec);
        var pinned = options.PinnedReplica is not null;
        int First(int i) => pinned ? options.PinnedReplica!.Value - 1 : i;
        var cases = new List<ConflictCase>();
        foreach (var intent in intents)
        {
            var bodies = Enumerable.Range(0, options.Variants).Select(v => Variant(intent, v)).ToList();
            var race = await Task.WhenAll(bodies.Select((b, v) => OrderOperations.SubmitAsync(api, b.Intent, First(intent.Index - 1 + v), pinned, ct)));
            var repeat = new SubmissionRecord[bodies.Count];
            for (var v = 0; v < bodies.Count; v++)
            {
                repeat[v] = await OrderOperations.SubmitAsync(api, bodies[v].Intent, First(intent.Index + v), pinned, ct);
            }

            var variants = bodies.Select((b, v) => new ConflictVariant(v, b.Change, b.Intent.PayloadSha256, b.Intent.Lines, b.Intent.CustomerId, race[v], repeat[v])).ToList();
            var verdict = Judge(intent, variants);
            if (options.Follow is { } follow && verdict.OrderId is { } orderId)
            {
                var followed = await OrderOperations.FollowAsync(api, orderId, intent.Index - 1, follow, ct);
                verdict = verdict with
                {
                    Follow = followed,
                    Problem = verdict.Problem ?? (followed.Reached ? null : followed.Error),
                    ExitCode = ExitCodes.Combine([verdict.ExitCode, followed.Reached ? ExitCodes.Ok : ExitCodes.ExpectationFailed]),
                };
            }

            cases.Add(verdict);
        }

        var summary = new
        {
            keys = cases.Count,
            variantsPerKey = options.Variants,
            keysWithOneWinner = cases.Count(c => c.WinnerVariant is not null),
            refusedInRace = cases.Sum(c => c.Variants.Count(v => v.Race.Acceptance == Acceptances.Conflict)),
            refusedOnRepeat = cases.Sum(c => c.Variants.Count(v => v.Repeat?.Acceptance == Acceptances.Conflict)),
            winnerReplayedOnRepeat = cases.Count(c => c.WinnerVariant is { } w && c.Variants[w].Repeat?.Acceptance == Acceptances.Replayed),
            keysAnsweredByTwoOrMoreReplicas = cases.Count(c => c.ServedBy.Count >= 2),
            byServer = cases.SelectMany(c => c.Variants.SelectMany(v => new[] { v.Race, v.Repeat })).Where(r => r?.ServedBy is not null)
                .GroupBy(r => r!.ServedBy!).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            completed = cases.Count(c => c.Follow?.FinalStatus == "Completed"),
            elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
        };
        return new RunResult("conflicts", RunModes.New, options.Spec.RunId, options.Spec, WorkloadGenerator.Digest(intents),
            api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(), started, DateTimeOffset.UtcNow, summary, cases,
            [.. cases.Where(c => c.Problem is not null).Select(c => $"#{c.Index} {c.IdempotencyKey}: {c.Problem}")], ExitCodes.Combine(cases.Select(c => c.ExitCode)));
    }

    /// <summary>Variant v of an intent under the same key: 0 unchanged; then customer, quantity, an extra line, line order, other customers.</summary>
    public static (string Change, OrderIntent Intent) Variant(OrderIntent intent, int v)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var lines = intent.Lines;
        var customer = intent.CustomerId;
        string change;
        switch (v)
        {
            case 0:
                change = "none";
                break;
            case 2:
                change = "quantity";
                var q = lines[0].Quantity;
                lines = [lines[0] with { Quantity = q > OrderLimits.MinQuantity ? q - 1 : q + 1 }, .. lines.Skip(1)];
                break;
            case 3 when lines.Count < OrderLimits.MaxLines:
                change = "extra-line";
                lines = [.. lines, new SubmitOrderLine("SKU-X" + v.ToString(CultureInfo.InvariantCulture), 1)];
                break;
            case 4 when lines.Count > 1:
                change = "line-order";
                lines = [.. lines.Reverse()];
                break;
            default:
                change = "customer";
                customer = $"{intent.CustomerId}-v{v.ToString(CultureInfo.InvariantCulture)}";
                break;
        }

        return (change, WorkloadGenerator.Create(intent.Index, intent.IdempotencyKey, customer, lines, StubPrices.TotalMinor(lines) >= StubPrices.DefaultReviewThresholdMinor));
    }

    public static ConflictCase Judge(OrderIntent intent, IReadOnlyList<ConflictVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(variants);
        var problems = new List<string>();
        var codes = new List<int>();
        var all = variants.SelectMany(v => new[] { v.Race, v.Repeat }).Where(r => r is not null).Select(r => r!).ToList();
        if (all.Any(r => r.Acceptance == Acceptances.NotSent))
        {
            return new ConflictCase(intent.Index, intent.IdempotencyKey, variants, null, null, "not sent (customer key missing)", ExitCodes.Usage);
        }

        if (all.Any(r => r.Acceptance == Acceptances.Unresolved))
        {
            problems.Add("some requests have no definite answer");
            codes.Add(ExitCodes.Unresolved);
        }

        // The winner is the variant whose body the key holds: accepted in the race (201), or 200 when
        // the key existed before this run.
        var winners = variants.Where(v => v.Race.Acceptance is Acceptances.Created or Acceptances.Replayed).ToList();
        var orderIds = all.Where(r => r.OrderId is not null).Select(r => r.OrderId!.Value).Distinct().ToList();
        int? winner = winners.Count == 1 ? winners[0].Variant : null;
        if (winners.Count != 1 || orderIds.Count > 1)
        {
            problems.Add($"{winners.Count} variants accepted in the race, {orderIds.Count} order ids: one key must hold one body and one order");
            codes.Add(ExitCodes.ExpectationFailed);
        }

        foreach (var v in variants.Where(v => v.Variant != winner))
        {
            if (v.Race.Acceptance is not (Acceptances.Conflict or Acceptances.Unresolved) && winners.Count == 1)
            {
                problems.Add($"variant {v.Variant} ({v.Change}) in the race: {v.Race.Acceptance} (http {v.Race.HttpStatus}), not 422");
                codes.Add(ExitCodes.ExpectationFailed);
            }

            if (v.Repeat is { Acceptance: not (Acceptances.Conflict or Acceptances.Unresolved) } r && winners.Count == 1)
            {
                problems.Add($"variant {v.Variant} ({v.Change}) repeated: {r.Acceptance} (http {r.HttpStatus}), not 422");
                codes.Add(ExitCodes.ExpectationFailed);
            }
        }

        if (winner is { } w && variants[w].Repeat is { } again && again.Acceptance != Acceptances.Unresolved
            && (again.Acceptance != Acceptances.Replayed || again.OrderId != orderIds.FirstOrDefault()))
        {
            problems.Add($"the accepted body repeated: {again.Acceptance} (http {again.HttpStatus}), not a replay of the same order");
            codes.Add(ExitCodes.ExpectationFailed);
        }

        return new ConflictCase(intent.Index, intent.IdempotencyKey, variants, winner, orderIds.Count == 1 ? orderIds[0] : null,
            problems.Count == 0 ? null : string.Join("; ", problems), ExitCodes.Combine(codes));
    }
}
