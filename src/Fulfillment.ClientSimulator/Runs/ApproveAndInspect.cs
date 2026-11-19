using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Reviews;

namespace Fulfillment.ClientSimulator.Runs;

/// <param name="Orders">Orders whose pending review is decided.</param>
/// <param name="Reviews">Reviews decided directly.</param>
/// <param name="Wait">How long to wait for an order's review to appear.</param>
/// <param name="DecisionSeed">Part of every decision id: the same command decides with the same ids.</param>
public sealed record ApproveOptions(
    IReadOnlyList<Guid> Orders,
    IReadOnlyList<Guid> Reviews,
    string Outcome = DecisionOutcomes.Approve,
    string OperatorId = "operator-1",
    string? Comment = null,
    TimeSpan Wait = default,
    string DecisionSeed = "default",
    int? PinnedReplica = null);

public static class DecisionResults
{
    public const string Accepted = "accepted";
    public const string Replayed = "replayed";
    public const string AlreadyDecided = "already-decided";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
    public const string DecisionIdReused = "decision-id-reused";
    public const string NotFound = "not-found";
    public const string NoPendingReview = "no-pending-review";
    public const string Refused = "refused";
    public const string Unresolved = "unresolved";
    public const string NotSent = "not-sent";
}

public sealed record DecisionRecord(Guid? OrderId, Guid? ReviewId, int? Round, Guid? DecisionId, string Result, string? Outcome, int? HttpStatus, string? Code,
    IReadOnlyList<AttemptRecord> Attempts, string? Problem, int ExitCode);

/// <summary>
/// The approve command: decides pending reviews through the operator review API. The decision id is
/// derived from the review, outcome, operator, and decision seed, so a repeated command (or a retry
/// after an ambiguous answer) sends the same decision and gets it back as a replay.
/// </summary>
public static class ApproveRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, ApproveOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var started = DateTimeOffset.UtcNow;
        var first = options.PinnedReplica is { } p ? p - 1 : 0;
        var pinned = options.PinnedReplica is not null;
        var records = new List<DecisionRecord>();
        foreach (var orderId in options.Orders)
        {
            var review = await PendingReviewAsync(api, orderId, options.Wait, first, pinned, ct);
            records.Add(review.Review is { } r
                ? await DecideAsync(api, orderId, r.ReviewId, r.Round, options, first, pinned, ct)
                : new DecisionRecord(orderId, null, null, null, review.Result, null, review.HttpStatus, review.Code, review.Attempts, review.Problem,
                    review.Result == DecisionResults.NotSent ? ExitCodes.Usage : review.Result == DecisionResults.Unresolved ? ExitCodes.Unresolved : ExitCodes.ExpectationFailed));
        }

        foreach (var reviewId in options.Reviews)
        {
            records.Add(await DecideAsync(api, null, reviewId, null, options, first, pinned, ct));
        }

        var summary = new
        {
            targets = records.Count,
            accepted = records.Count(r => r.Result == DecisionResults.Accepted),
            replayed = records.Count(r => r.Result == DecisionResults.Replayed),
            failed = records.Count(r => r.ExitCode != ExitCodes.Ok),
            outcome = options.Outcome,
            operatorId = options.OperatorId,
        };
        return new RunResult("approve", options.Outcome, null, null, null, api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(),
            started, DateTimeOffset.UtcNow, summary, records, [.. records.Where(r => r.Problem is not null).Select(r => r.Problem!)],
            ExitCodes.Combine(records.Select(r => r.ExitCode)));
    }

    /// <summary>A stable decision id (RFC 9562 version 8 layout over SHA-256).</summary>
    public static Guid DecisionId(Guid reviewId, string outcome, string operatorId, string decisionSeed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"client-simulator-decision-v1\n{reviewId:N}\n{outcome}\n{operatorId}\n{decisionSeed}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private sealed record ReviewLookup(ReviewView? Review, string Result, int? HttpStatus, string? Code, IReadOnlyList<AttemptRecord> Attempts, string? Problem);

    private static async Task<ReviewLookup> PendingReviewAsync(ApiClient api, Guid orderId, TimeSpan wait, int first, bool pinned, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Get, $"{ReviewRoutes.Reviews}?orderId={orderId}", null, null, first, pinned, ct);
            if (call.Status != 200)
            {
                var result = call.Attempts[^1].Class switch
                {
                    AttemptClasses.NotSent => DecisionResults.NotSent,
                    AttemptClasses.Ambiguous => DecisionResults.Unresolved,
                    _ => DecisionResults.Refused,
                };
                return new ReviewLookup(null, result, call.Status, call.Code, call.Attempts, $"order {orderId}: review lookup {result} ({call.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? call.Code})");
            }

            var reviews = JsonSerializer.Deserialize<List<ReviewView>>(call.Body!, ResultJson.Options) ?? [];
            if (reviews.FirstOrDefault(r => r.Status == "Pending") is { } pending)
            {
                return new ReviewLookup(pending, DecisionResults.Accepted, call.Status, null, call.Attempts, null);
            }

            // Already decided: the same command decides again with the same decision id, so the server
            // answers with a replay (the same decision) or 409 already decided (another one). An expired
            // latest round may be followed by a new one: keep waiting for it.
            var latest = reviews.OrderByDescending(r => r.Round).FirstOrDefault();
            if (latest is { Status: "Approved" or "Rejected" })
            {
                return new ReviewLookup(latest, DecisionResults.Accepted, call.Status, null, call.Attempts, null);
            }

            if (watch.Elapsed >= wait)
            {
                return new ReviewLookup(null, DecisionResults.NoPendingReview, call.Status, null, call.Attempts,
                    latest is null ? $"order {orderId}: no review" : $"order {orderId}: no pending review (round {latest.Round} is {latest.Status})");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static async Task<DecisionRecord> DecideAsync(ApiClient api, Guid? orderId, Guid reviewId, int? round, ApproveOptions options, int first, bool pinned, CancellationToken ct)
    {
        var decisionId = DecisionId(reviewId, options.Outcome, options.OperatorId, options.DecisionSeed);
        var body = JsonSerializer.Serialize(new SubmitDecisionRequest(decisionId, options.OperatorId, options.Outcome, options.Comment), ResultJson.Options);
        var call = await api.SendAsync(ApiRole.Operator, HttpMethod.Post, ReviewRoutes.Decision(reviewId), body, null, first, pinned, ct);
        string? outcome = null;
        string result;
        if (call.Attempts[^1].Class == AttemptClasses.NotSent)
        {
            result = DecisionResults.NotSent;
        }
        else if (call.Unresolved)
        {
            result = DecisionResults.Unresolved;
        }
        else if (call.Status == 200)
        {
            var accepted = JsonSerializer.Deserialize<DecisionAccepted>(call.Body!, ResultJson.Options)!;
            outcome = accepted.Outcome;
            result = accepted.Replayed ? DecisionResults.Replayed : DecisionResults.Accepted;
        }
        else
        {
            result = call.Code switch
            {
                ReviewErrorCodes.ReviewAlreadyDecided => DecisionResults.AlreadyDecided,
                ReviewErrorCodes.ReviewExpired => DecisionResults.Expired,
                ReviewErrorCodes.ReviewSuperseded => DecisionResults.Superseded,
                ReviewErrorCodes.DecisionIdReused => DecisionResults.DecisionIdReused,
                ReviewErrorCodes.ReviewNotFound => DecisionResults.NotFound,
                _ => DecisionResults.Refused,
            };
        }

        var exit = result switch
        {
            DecisionResults.Accepted or DecisionResults.Replayed => ExitCodes.Ok,
            DecisionResults.NotSent => ExitCodes.Usage,
            DecisionResults.Unresolved => ExitCodes.Unresolved,
            _ => ExitCodes.ExpectationFailed,
        };
        return new DecisionRecord(orderId, reviewId, round, decisionId, result, outcome, call.Status, call.Code, call.Attempts,
            exit == ExitCodes.Ok ? null : $"review {reviewId}: {result} (http {call.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} {call.Code})", exit);
    }
}

public sealed record InspectSection(string State, int? HttpStatus, JsonElement? Data, string? Reason);

public sealed record InspectRecord(Guid OrderId, InspectSection Order, InspectSection Reviews, InspectSection Execution, int ExitCode);

/// <summary>
/// The inspect command: the business view (customer key), the order's reviews (operator key), and the
/// execution diagnostics (admin key). A section whose key is not configured is skipped, not guessed.
/// </summary>
public static class InspectRun
{
    public static async Task<RunResult> RunAsync(ApiClient api, IReadOnlyList<Guid> orders, int? pinnedReplica, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(orders);
        var started = DateTimeOffset.UtcNow;
        var first = pinnedReplica is { } p ? p - 1 : 0;
        var pinned = pinnedReplica is not null;
        var records = new List<InspectRecord>();
        foreach (var orderId in orders)
        {
            var order = await SectionAsync(api, ApiRole.Customer, $"{OrderRoutes.Orders}/{orderId}", first, pinned, ct);
            var reviews = await SectionAsync(api, ApiRole.Operator, $"{ReviewRoutes.Reviews}?orderId={orderId}", first, pinned, ct);
            var execution = await SectionAsync(api, ApiRole.Admin, $"/system/orders/{orderId}/execution", first, pinned, ct);
            var exit = order.State switch
            {
                "ok" => ExitCodes.Ok,
                "skipped" => ExitCodes.Usage,
                "unresolved" => ExitCodes.Unresolved,
                _ => ExitCodes.ExpectationFailed,
            };
            records.Add(new InspectRecord(orderId, order, reviews, execution, exit));
        }

        var summary = new
        {
            orders = records.Count,
            found = records.Count(r => r.Order.State == "ok"),
            statuses = records.Where(r => r.Order.Data is not null)
                .GroupBy(r => r.Order.Data!.Value.GetProperty("status").GetString() ?? "unknown")
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            reviewsShown = records.Count(r => r.Reviews.State == "ok"),
            executionShown = records.Count(r => r.Execution.State == "ok"),
        };
        var problems = records.Where(r => r.ExitCode != ExitCodes.Ok).Select(r => $"order {r.OrderId}: {r.Order.State} {r.Order.Reason}").ToList();
        return new RunResult("inspect", "read", null, null, null, api.Endpoints.Select(e => new EndpointInfo(e.Name, e.BaseUrl)).ToList(),
            started, DateTimeOffset.UtcNow, summary, records, problems, ExitCodes.Combine(records.Select(r => r.ExitCode)));
    }

    private static async Task<InspectSection> SectionAsync(ApiClient api, ApiRole role, string path, int first, bool pinned, CancellationToken ct)
    {
        if (api.Credentials.KeyOf(role) is null)
        {
            return new InspectSection("skipped", null, null, $"{Credentials.VariableOf(role)} is not set");
        }

        var call = await api.SendAsync(role, HttpMethod.Get, path, null, null, first, pinned, ct);
        if (call.Unresolved)
        {
            return new InspectSection("unresolved", call.Status, null, call.Attempts[^1].Error ?? call.Code);
        }

        if (call.Status == 200)
        {
            using var document = JsonDocument.Parse(call.Body!);
            return new InspectSection("ok", 200, document.RootElement.Clone(), null);
        }

        return new InspectSection(call.Status == 404 ? "not-found" : "error", call.Status, null, call.Code ?? (call.Status == 401 ? "key not accepted" : null));
    }
}
