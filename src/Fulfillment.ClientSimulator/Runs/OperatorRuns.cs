using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.ClientSimulator.Runs;

/// <summary>One answered (or unanswered) operator or customer request of the M3 commands.</summary>
public sealed record OperatorCallRecord(string Target, string Action, string Result, int? HttpStatus, string? Code, JsonElement? Data,
    IReadOnlyList<AttemptRecord> Attempts, int ExitCode);

/// <summary>Shared mapping of a call to a record: 2xx ok, ambiguous unresolved, a credential problem usage, other answers expectation failed.</summary>
internal static class OperatorCalls
{
    public static OperatorCallRecord Record(string target, string action, CallResult call, params int[] okStatuses)
    {
        JsonElement? data = null;
        if (call.Body is { Length: > 0 } body && body[0] is '{' or '[')
        {
            using var document = JsonDocument.Parse(body);
            data = document.RootElement.Clone();
        }

        if (call.Attempts[^1].Class == AttemptClasses.NotSent)
        {
            return new OperatorCallRecord(target, action, "not-sent", null, call.Code, null, call.Attempts, ExitCodes.Usage);
        }

        if (call.Unresolved)
        {
            return new OperatorCallRecord(target, action, "unresolved", call.Status, call.Code, null, call.Attempts, ExitCodes.Unresolved);
        }

        var ok = call.Status is { } s && okStatuses.Contains(s);
        return new OperatorCallRecord(target, action, ok ? "ok" : call.Code ?? $"http-{call.Status?.ToString(CultureInfo.InvariantCulture)}", call.Status, call.Code, data,
            call.Attempts, ok ? ExitCodes.Ok : ExitCodes.ExpectationFailed);
    }

    public static RunResult Result(ApiClient api, string command, string mode, DateTimeOffset started, object summary, IReadOnlyList<OperatorCallRecord> records) =>
        new(command, mode, null, null, null, api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(), started, DateTimeOffset.UtcNow, summary, records,
            [.. records.Where(r => r.ExitCode != ExitCodes.Ok).Select(r => $"{r.Target}: {r.Action} {r.Result}")], ExitCodes.Combine(records.Select(r => r.ExitCode)));

    /// <summary>A stable id (RFC 9562 version 8 layout over SHA-256) from the given parts.</summary>
    public static Guid StableId(string domain, params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\n" + string.Join('\n', parts)));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

/// <summary>The cancel command (customer key): 202 recorded or 200 already requested are ok; 409 dispatch-started or order-terminal and 404 are answers that differ.</summary>
public static class CancelRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, IReadOnlyList<Guid> orders, int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(orders);
        var started = DateTimeOffset.UtcNow;
        var records = new List<OperatorCallRecord>();
        foreach (var orderId in orders)
        {
            var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, $"{OrderRoutes.Orders}/{orderId}/cancel", null, null, (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
            var record = OperatorCalls.Record(orderId.ToString(), "cancel", call, 200, 202);
            records.Add(record.Result == "ok" ? record with { Result = call.Status == 202 ? "recorded" : "already-requested" } : record);
        }

        var summary = new
        {
            orders = records.Count,
            recorded = records.Count(r => r.Result == "recorded"),
            alreadyRequested = records.Count(r => r.Result == "already-requested"),
            dispatchStarted = records.Count(r => r.Code == OrderErrorCodes.DispatchStarted),
            orderTerminal = records.Count(r => r.Code == OrderErrorCodes.OrderTerminal),
        };
        return OperatorCalls.Result(api, "cancel", "cancel", started, summary, records);
    }
}

/// <summary>The reviews command (operator key): the reviews of the named orders, or every review with a status.</summary>
public static class ReviewsRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, IReadOnlyList<Guid> orders, string? status, int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(orders);
        var started = DateTimeOffset.UtcNow;
        var queries = orders.Count > 0
            ? orders.Select(o => (o.ToString(), $"{ReviewRoutes.Reviews}?orderId={o}" + (status is null ? "" : $"&status={Uri.EscapeDataString(status)}"))).ToList()
            : [("all", ReviewRoutes.Reviews + (status is null ? "" : $"?status={Uri.EscapeDataString(status)}"))];
        var records = new List<OperatorCallRecord>();
        foreach (var (target, path) in queries)
        {
            var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Get, path, null, null, (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
            records.Add(OperatorCalls.Record(target, "list", call, 200));
        }

        var views = records.Where(r => r.Data is { ValueKind: JsonValueKind.Array }).SelectMany(r => r.Data!.Value.Deserialize<List<ReviewView>>(ResultJson.Options) ?? []).ToList();
        var summary = new
        {
            reviews = views.Count,
            statuses = views.GroupBy(v => v.Status).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        };
        return OperatorCalls.Result(api, "reviews", status ?? "all", started, summary, records);
    }
}

/// <summary>The escalate command (operator key): escalates a pending round before its deadline; a repeat returns the same next round.</summary>
public static class EscalateRun
{
    public static async Task<OperatorCallRecord> EscalateAsync(ApiClient api, Guid reviewId, string operatorId, string reason, int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        var body = JsonSerializer.Serialize(new EscalateReviewRequest(operatorId, reason), ResultJson.Options);
        var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Post, $"{ReviewRoutes.Reviews}/{reviewId}/escalate", body, null, (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
        var record = OperatorCalls.Record(reviewId.ToString(), "escalate", call, 200);
        if (record.Result == "ok" && record.Data is { } data)
        {
            record = record with { Result = data.GetProperty("replayed").GetBoolean() ? "replayed" : "escalated" };
        }

        return record;
    }

    public static async Task<RunResult> RunAsync(ApiClient api, IReadOnlyList<Guid> orders, IReadOnlyList<Guid> reviews, string operatorId, string reason,
        int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(reviews);
        var started = DateTimeOffset.UtcNow;
        var records = new List<OperatorCallRecord>();
        foreach (var orderId in orders)
        {
            var pending = await OperatorRun.PendingReviewAsync(api, orderId, TimeSpan.Zero, pinnedReplica, ct);
            records.Add(pending.Review is { } review
                ? await EscalateAsync(api, review.ReviewId, operatorId, reason, pinnedReplica, ct)
                : pending.Record);
        }

        foreach (var reviewId in reviews)
        {
            records.Add(await EscalateAsync(api, reviewId, operatorId, reason, pinnedReplica, ct));
        }

        var summary = new
        {
            targets = records.Count,
            escalated = records.Count(r => r.Result == "escalated"),
            replayed = records.Count(r => r.Result == "replayed"),
            noFurtherRound = records.Count(r => r.Code == ReviewErrorCodes.NoFurtherRound),
        };
        return OperatorCalls.Result(api, "escalate", "escalate", started, summary, records);
    }
}

/// <summary>
/// The resolve command (operator key): resolves an intervention case. The resolution id is derived from the case,
/// action, operator, and a resolution seed, so a repeated command is a replay and never a second resolution.
/// </summary>
public static class ResolveRun
{
    public static Guid ResolutionId(Guid caseId, string action, string operatorId, string seed) =>
        OperatorCalls.StableId("client-simulator-resolution-v1", caseId.ToString("N"), action, operatorId, seed);

    public static async Task<RunResult> RunAsync(ApiClient api, IReadOnlyList<Guid> cases, string action, string operatorId, string evidence, string seed,
        int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(cases);
        var started = DateTimeOffset.UtcNow;
        var records = new List<OperatorCallRecord>();
        foreach (var caseId in cases)
        {
            var body = JsonSerializer.Serialize(new ResolveCaseRequest(ResolutionId(caseId, action, operatorId, seed), operatorId, action, evidence), ResultJson.Options);
            var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Post, $"{InterventionRoutes.Interventions}/{caseId}/resolution", body, null,
                (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
            var record = OperatorCalls.Record(caseId.ToString(), action, call, 200);
            if (record.Result == "ok" && record.Data is { } data)
            {
                record = record with { Result = (data.GetProperty("replayed").GetBoolean() ? "replayed:" : "resolved:") + data.GetProperty("path").GetString() };
            }

            records.Add(record);
        }

        var summary = new
        {
            cases = records.Count,
            resolved = records.Count(r => r.Result.StartsWith("resolved:", StringComparison.Ordinal)),
            replayed = records.Count(r => r.Result.StartsWith("replayed:", StringComparison.Ordinal)),
            action,
        };
        return OperatorCalls.Result(api, "resolve", action, started, summary, records);
    }
}

/// <summary>Weights of the scripted operator's choices (each at least 0, not all 0).</summary>
public sealed record OperatorWeights(double Approve, double Reject, double Escalate, double Ignore)
{
    public double Total => Approve + Reject + Escalate + Ignore;
}

/// <summary>A planned choice for one order: what the scripted operator does with its pending review.</summary>
public sealed record OperatorChoice(Guid OrderId, string Action, string OperatorId);

/// <summary>An executed choice: the requests sent and what came back.</summary>
public sealed record OperatorRunRecord(Guid OrderId, string Action, string OperatorId, IReadOnlyList<OperatorCallRecord> Calls, string Result, int ExitCode);

/// <summary>
/// The seeded scripted operator (operator run): the same seed, weights, operators, and orders give the same plan
/// (recorded with its SHA-256 digest) and the same decision ids, so a repeated run is a replay: decisions come back
/// as replays and escalations return the same next round. Choices: approve, reject, escalate (then approve the next
/// round), ignore (the deadline policy decides).
/// </summary>
public static class OperatorRun
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Escalate = "escalate";
    public const string Ignore = "ignore";

    public static IReadOnlyList<OperatorChoice> Plan(IReadOnlyList<Guid> orders, ulong seed, OperatorWeights weights, IReadOnlyList<string> operators)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(operators);
        if (operators.Count == 0 || weights.Total <= 0)
        {
            throw new ArgumentException("at least one operator and a positive weight");
        }

        // Each order's choice depends only on the seed and the order id, not on the order's position or the other orders.
        return [.. orders.Select(orderId =>
        {
            var random = DeterministicRandom.ForItem(seed, BitConverter.ToInt64(SHA256.HashData(orderId.ToByteArray(bigEndian: true)), 0));
            var x = random.NextDouble() * weights.Total;
            var action = x < weights.Approve ? Approve
                : x < weights.Approve + weights.Reject ? Reject
                : x < weights.Approve + weights.Reject + weights.Escalate ? Escalate
                : Ignore;
            return new OperatorChoice(orderId, action, operators[random.Next(operators.Count)]);
        })];
    }

    public static string Digest(IReadOnlyList<OperatorChoice> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var text = string.Join('\n', plan.Select(c => $"{c.OrderId:N} {c.Action} {c.OperatorId}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    internal sealed record Pending(ReviewView? Review, OperatorCallRecord Record);

    /// <summary>The order's pending round (waiting up to <paramref name="wait"/> for one to appear).</summary>
    internal static async Task<Pending> PendingReviewAsync(ApiClient api, Guid orderId, TimeSpan wait, int? pinnedReplica, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Get, $"{ReviewRoutes.Reviews}?orderId={orderId}", null, null,
                (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
            var record = OperatorCalls.Record(orderId.ToString(), "lookup", call, 200);
            if (record.ExitCode != ExitCodes.Ok)
            {
                return new Pending(null, record);
            }

            var reviews = JsonSerializer.Deserialize<List<ReviewView>>(call.Body!, ResultJson.Options) ?? [];
            if (reviews.FirstOrDefault(r => r.Status == ReviewStates.Pending) is { } pending)
            {
                return new Pending(pending, record);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var latest = reviews.OrderByDescending(r => r.Round).FirstOrDefault();
                return new Pending(latest, record with { Result = "no-pending-review", ExitCode = ExitCodes.ExpectationFailed });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    public static async Task<RunResult> RunAsync(ApiClient api, IReadOnlyList<OperatorChoice> plan, ulong seed, TimeSpan wait, int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(plan);
        var started = DateTimeOffset.UtcNow;
        var decisionSeed = "operator-run-" + seed.ToString(CultureInfo.InvariantCulture);
        var records = new List<OperatorRunRecord>();
        foreach (var choice in plan)
        {
            var calls = new List<OperatorCallRecord>();
            if (choice.Action == Ignore)
            {
                records.Add(new OperatorRunRecord(choice.OrderId, choice.Action, choice.OperatorId, calls, "ignored", ExitCodes.Ok));
                continue;
            }

            var pending = await PendingReviewAsync(api, choice.OrderId, wait, pinnedReplica, ct);
            calls.Add(pending.Record);
            var review = pending.Review;
            if (review is null && pending.Record.Result == "no-pending-review")
            {
                // The order never had a review (auto-approved by the policy): nothing for the operator to do.
                records.Add(new OperatorRunRecord(choice.OrderId, choice.Action, choice.OperatorId, calls, "no-review", ExitCodes.Ok));
                continue;
            }

            if (review is null || (pending.Record.ExitCode != ExitCodes.Ok && !Repeatable(choice, review)))
            {
                records.Add(new OperatorRunRecord(choice.OrderId, choice.Action, choice.OperatorId, calls, pending.Record.Result, pending.Record.ExitCode));
                continue;
            }

            if (choice.Action == Escalate)
            {
                // A repeated run finds round 1 Escalated (and round 2 decided): escalating round 1 again returns the same round 2.
                var round1 = review.Round == 1 ? review.ReviewId : (await RoundAsync(api, choice.OrderId, 1, pinnedReplica, ct)) ?? review.ReviewId;
                var escalated = await EscalateRun.EscalateAsync(api, round1, choice.OperatorId, "scripted operator escalation", pinnedReplica, ct);
                calls.Add(escalated);
                if (escalated.ExitCode != ExitCodes.Ok)
                {
                    records.Add(new OperatorRunRecord(choice.OrderId, choice.Action, choice.OperatorId, calls, escalated.Result, escalated.ExitCode));
                    continue;
                }

                var next = escalated.Data!.Value.GetProperty("nextReviewId").GetGuid();
                calls.Add(await DecideAsync(api, next, Approve, choice.OperatorId, decisionSeed, pinnedReplica, ct));
            }
            else
            {
                calls.Add(await DecideAsync(api, review.ReviewId, choice.Action, choice.OperatorId, decisionSeed, pinnedReplica, ct));
            }

            var last = calls[^1];
            records.Add(new OperatorRunRecord(choice.OrderId, choice.Action, choice.OperatorId, calls, last.Result, last.ExitCode));
        }

        var summary = new
        {
            orders = records.Count,
            planDigest = Digest(plan),
            seed,
            actions = records.GroupBy(r => r.Action).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            results = records.GroupBy(r => r.Result).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            failed = records.Count(r => r.ExitCode != ExitCodes.Ok),
        };
        return new RunResult("operator", "run", null, null, Digest(plan), api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(), started,
            DateTimeOffset.UtcNow, summary, records, [.. records.Where(r => r.ExitCode != ExitCodes.Ok).Select(r => $"order {r.OrderId}: {r.Action} {r.Result}")],
            ExitCodes.Combine(records.Select(r => r.ExitCode)));
    }

    /// <summary>A repeated run meets decided or escalated rounds; it repeats the same requests and gets replays back.</summary>
    private static bool Repeatable(OperatorChoice choice, ReviewView latest) =>
        choice.Action switch
        {
            Escalate => latest.Round >= 2,
            _ => latest.Status is ReviewStates.Approved or ReviewStates.Rejected,
        };

    private static async Task<Guid?> RoundAsync(ApiClient api, Guid orderId, int round, int? pinnedReplica, CancellationToken ct)
    {
        var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Get, $"{ReviewRoutes.Reviews}?orderId={orderId}", null, null, (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
        return call.Status == 200
            ? (JsonSerializer.Deserialize<List<ReviewView>>(call.Body!, ResultJson.Options) ?? []).FirstOrDefault(r => r.Round == round)?.ReviewId
            : null;
    }

    private static async Task<OperatorCallRecord> DecideAsync(ApiClient api, Guid reviewId, string outcome, string operatorId, string decisionSeed, int? pinnedReplica,
        CancellationToken ct)
    {
        var decisionId = ApproveRun.DecisionId(reviewId, outcome, operatorId, decisionSeed);
        var body = JsonSerializer.Serialize(new SubmitDecisionRequest(decisionId, operatorId, outcome, "scripted operator"), ResultJson.Options);
        var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Post, ReviewRoutes.Decision(reviewId), body, null, (pinnedReplica ?? 1) - 1, pinnedReplica is not null, ct);
        var record = OperatorCalls.Record(reviewId.ToString(), outcome, call, 200);
        if (record.Result == "ok" && record.Data is { } data)
        {
            record = record with { Result = data.GetProperty("replayed").GetBoolean() ? DecisionResults.Replayed : DecisionResults.Accepted };
        }

        return record;
    }
}
