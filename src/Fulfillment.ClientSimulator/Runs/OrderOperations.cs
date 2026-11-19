using System.Diagnostics;
using System.Text.Json;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.ClientSimulator.Runs;

public enum Distribution
{
    /// <summary>Order i goes to replica (i - 1) mod n.</summary>
    RoundRobin,

    /// <summary>Each order's replica is drawn from the seed (reproducible, uneven).</summary>
    Seeded,
}

/// <summary>Which replica receives an intent first: a pure function of the run, so a replay sends each request to the same place.</summary>
public static class ReplicaPlan
{
    public static int FirstEndpoint(Distribution distribution, ulong seed, int index, int endpointCount, int? pinnedReplica)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(endpointCount);
        if (pinnedReplica is { } pinned)
        {
            return pinned - 1;
        }

        return distribution == Distribution.Seeded
            ? DeterministicRandom.ForItem(seed ^ 0x5EED_5EED_5EED_5EEDUL, index).Next(endpointCount)
            : (index - 1) % endpointCount;
    }
}

public sealed record FollowOptions(string Until, TimeSpan Timeout, TimeSpan PollInterval)
{
    public const string Terminal = "terminal";
    public const string Review = "review";

    public static FollowOptions Default { get; } = new(Terminal, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1));
}

public static class OrderOperations
{
    /// <summary>Statuses after which an order never changes again.</summary>
    public static readonly IReadOnlySet<string> TerminalStatuses = new HashSet<string>(StringComparer.Ordinal) { "Completed", "Rejected", "Cancelled", "Compensated" };

    /// <summary>Submits one intent (with retries of ambiguous attempts) and classifies the answer.</summary>
    public static async Task<SubmissionRecord> SubmitAsync(ApiClient api, OrderIntent intent, int firstEndpoint, bool pinned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(intent);
        var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Post, OrderRoutes.Orders, intent.Body, intent.IdempotencyKey, firstEndpoint, pinned, ct);
        Guid? orderId = null;
        string acceptance;
        if (call.Attempts.Count > 0 && call.Attempts[^1].Class == AttemptClasses.NotSent)
        {
            acceptance = Acceptances.NotSent;
        }
        else if (call.Unresolved)
        {
            acceptance = Acceptances.Unresolved;
        }
        else if (call.Status is 200 or 201)
        {
            var accepted = JsonSerializer.Deserialize<OrderAccepted>(call.Body!, ResultJson.Options);
            orderId = accepted?.OrderId;
            acceptance = call.Status == 201 ? Acceptances.Created : Acceptances.Replayed;
        }
        else if (call.Status == 422 && call.Code == OrderErrorCodes.IdempotencyKeyReused)
        {
            acceptance = Acceptances.Conflict;
        }
        else
        {
            acceptance = Acceptances.Refused;
        }

        return new SubmissionRecord(intent.Index, intent.IdempotencyKey, intent.CustomerId, intent.Lines, intent.PayloadSha256, intent.RequiresReview,
            api.Endpoints[api.Wrap(firstEndpoint)].Name, acceptance, orderId, call.Status, call.Code, call.Attempts);
    }

    /// <summary>Polls the order (any replica, in turn) until it is terminal, or waits for review when asked, or the time is up.</summary>
    public static async Task<FollowRecord> FollowAsync(ApiClient api, Guid orderId, int firstEndpoint, FollowOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var watch = Stopwatch.StartNew();
        string? status = null;
        IReadOnlyList<string> history = [];
        string? error = null;
        for (var poll = 0; ; poll++)
        {
            var call = await api.SendAsync(ApiRole.Customer, HttpMethod.Get, $"{OrderRoutes.Orders}/{orderId}", null, null, firstEndpoint + poll, false, ct);
            if (call.Status == 200)
            {
                var view = JsonSerializer.Deserialize<OrderView>(call.Body!, ResultJson.Options)!;
                status = view.Status;
                history = [.. view.History.Select(h => h.ToStatus)];
                error = null;
                if (TerminalStatuses.Contains(status) || (options.Until == FollowOptions.Review && status == "AwaitingApproval"))
                {
                    return new FollowRecord(status, true, history, watch.ElapsedMilliseconds, null);
                }
            }
            else
            {
                error = call.Status is { } s ? $"http {s}" : call.Code;
                if (call.Attempts[^1].Class == AttemptClasses.NotSent)
                {
                    break;
                }
            }

            if (watch.Elapsed + options.PollInterval > options.Timeout)
            {
                break;
            }

            await Task.Delay(options.PollInterval, ct);
        }

        return new FollowRecord(status, false, history, watch.ElapsedMilliseconds,
            error ?? $"not {(options.Until == FollowOptions.Review ? "terminal or waiting for review" : "terminal")} within {options.Timeout.TotalSeconds:0} s");
    }
}
