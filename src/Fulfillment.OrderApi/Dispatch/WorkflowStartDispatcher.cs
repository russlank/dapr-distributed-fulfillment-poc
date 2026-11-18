using System.Diagnostics.Metrics;
using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Fulfillment.OrderApi.Dispatch;

public sealed class StartDispatcherOptions
{
    public const string Section = "StartDispatcher";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Requests claimed per poll at most.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>Starts in flight per dispatcher at most; a slow start never stops the dispatcher from polling.</summary>
    public int MaxInFlight { get; set; } = 32;

    /// <summary>How long a claim is exclusive; a dispatcher that dies loses it after this time.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delay after a refusal (4xx): a configuration problem, not a transient one.</summary>
    public TimeSpan RejectedBackoff { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Failpoint names of the dispatcher (drill builds only).</summary>
public static class DispatcherFailpoints
{
    /// <summary>After the attempt is recorded, before the workflow host is called (key: order id, N format).</summary>
    public const string BeforeWorkflowStartCall = "BeforeWorkflowStartCall";
}

/// <summary>
/// Durable workflow-start dispatcher (runs in every order-api replica). Each poll claims due Pending
/// start requests, closes those whose order is terminal, and starts the rest through the workflow
/// host. Outcomes: started or already existing → Started; ambiguous → released with backoff (the
/// next attempt's state-first start resolves it); refused → released with a long backoff. A crash
/// leaves the lease to expire; another dispatcher claims the request again. Every change after the
/// claim is fenced by the lease token, so a dispatcher that lost its lease changes nothing, and the
/// execution claim in the workflow stays the authority against duplicate execution.
/// </summary>
public sealed partial class WorkflowStartDispatcher(
    IServiceScopeFactory scopes,
    WorkflowStartStore store,
    Failpoints failpoints,
    IOptions<StartDispatcherOptions> options,
    RuntimeIdentity identity,
    ILogger<WorkflowStartDispatcher> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Fulfillment.OrderApi.Dispatch");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("workflow_start_attempts_total", description: "start attempts by result");
    private static readonly Counter<long> ClosedTerminal = Meter.CreateCounter<long>("workflow_start_requests_closed_terminal_total",
        description: "start requests closed because the order was already terminal");

    public string DispatcherId { get; } = $"{identity.Replica}/{Guid.NewGuid():N}"[..Math.Min(identity.Replica.Length + 33, 64)];

    /// <summary>Exponential backoff with jitter, capped: base·2^(attempt-1) up to max, plus up to 20%.</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan max, double jitter)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var delay = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, exponent), max.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delay * (1 + (0.2 * Math.Clamp(jitter, 0, 1))));
    }

    /// <summary>One poll, awaited to the end: claim, guard, start. Returns the number of requests claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var started = await ClaimAndDispatchAsync(options.Value.BatchSize, ct);
        await Task.WhenAll(started);
        return started.Count;
    }

    /// <summary>Claims up to <paramref name="capacity"/> requests, closes terminal ones, and returns the started dispatch tasks.</summary>
    private async Task<IReadOnlyList<Task>> ClaimAndDispatchAsync(int capacity, CancellationToken ct)
    {
        var settings = options.Value;
        var batch = await store.ClaimDueAsync(DispatcherId, capacity, settings.Lease, ct);
        foreach (var orderId in batch.ClosedTerminal)
        {
            ClosedTerminal.Add(1);
            LogClosedTerminal(logger, orderId);
        }

        return [.. batch.Claimed.Select(claim => DispatchAsync(claim, settings, ct))];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        LogStarted(logger, DispatcherId, settings.Lease.TotalSeconds);
        var inFlight = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            inFlight.RemoveAll(task => task.IsCompleted);
            var capacity = Math.Min(settings.BatchSize, settings.MaxInFlight - inFlight.Count);
            var claimedFull = false;
            if (capacity > 0)
            {
                try
                {
                    var started = await ClaimAndDispatchAsync(capacity, stoppingToken);
                    inFlight.AddRange(started);
                    claimedFull = started.Count == capacity;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    // SQL or other infrastructure trouble: claimed leases expire and are retried.
                    LogPollFailed(logger, e);
                }
            }

            if (claimedFull)
            {
                continue; // more due work: poll again at once
            }

            try
            {
                // Jittered (0.5-1.5 × interval): without it a dispatcher polls in phase with its own
                // claims and always wins the re-claim of its own expired leases (observed), so a
                // stuck replica would keep its work instead of another replica taking it over.
                await Task.Delay(settings.PollInterval * (0.5 + Random.Shared.NextDouble()), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Shutting down: in-flight starts end on cancellation; unfinished leases expire.
        await Task.WhenAll(inFlight);
    }

    private async Task DispatchAsync(ClaimedStartRequest claim, StartDispatcherOptions settings, CancellationToken ct)
    {
        string result;
        string? reported = null;
        long attemptId = 0;
        try
        {
            attemptId = await store.RecordAttemptAsync(claim, DispatcherId, ct);
            await failpoints.HitAsync(DispatcherFailpoints.BeforeWorkflowStartCall, claim.OrderId.ToString("N"), ct);

            await using var scope = scopes.CreateAsyncScope();
            var starter = scope.ServiceProvider.GetRequiredService<IWorkflowStarter>();
            var outcome = await starter.StartAsync(claim.InstanceId, new FulfillmentInput(claim.OrderId), ct);
            reported = outcome.ReportedStatus;
            result = outcome.Kind switch
            {
                StartOutcomeKind.Started => Result(await store.MarkStartedAsync(claim, ct), StartAttemptResults.Started),
                StartOutcomeKind.AlreadyExists => Result(await store.MarkStartedAsync(claim, ct), StartAttemptResults.AlreadyExists),
                StartOutcomeKind.Rejected => Result(await store.ReleaseForRetryAsync(claim, StartErrorCodes.StartRejected, settings.RejectedBackoff, ct),
                    StartAttemptResults.Rejected),
                _ => Result(await store.ReleaseForRetryAsync(claim, StartErrorCodes.StartAmbiguous,
                        Backoff(claim.Attempt, settings.BaseBackoff, settings.MaxBackoff, Random.Shared.NextDouble()), ct),
                    StartAttemptResults.Ambiguous),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // shutting down: the lease expires
        }
        catch (Exception e)
        {
            // Includes SQL failures after the call: the lease expires and the next attempt's
            // state-first start finds an instance this attempt created.
            LogAttemptFailed(logger, claim.OrderId, claim.Attempt, e);
            result = StartAttemptResults.Ambiguous;
        }

        Attempts.Add(1, new KeyValuePair<string, object?>("result", result));
        switch (result)
        {
            case StartAttemptResults.LeaseLost:
                LogLeaseLost(logger, claim.OrderId, claim.Attempt);
                break;
            case StartAttemptResults.AlreadyClosed:
                LogAlreadyClosed(logger, claim.OrderId, claim.Attempt, reported);
                break;
            case StartAttemptResults.Unexpected:
                LogUnexpected(logger, claim.OrderId, claim.Attempt, reported);
                break;
            default:
                LogAttempt(logger, claim.OrderId, claim.Attempt, result, reported);
                break;
        }

        if (attemptId != 0)
        {
            try
            {
                await store.CompleteAttemptAsync(attemptId, result, reported, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogAttemptFailed(logger, claim.OrderId, claim.Attempt, e);
            }
        }
    }

    /// <summary>
    /// Result of an attempt from what its fenced update found. A request closed meanwhile is not a lost
    /// lease: the order became terminal (a fast workflow can finish before the start is acknowledged),
    /// and the terminal state stands; the attempt changes nothing.
    /// </summary>
    public static string Result(FencedUpdateOutcome update, string applied) => update switch
    {
        FencedUpdateOutcome.Applied => applied,
        FencedUpdateOutcome.LeaseLost => StartAttemptResults.LeaseLost,
        FencedUpdateOutcome.AlreadyClosed => StartAttemptResults.AlreadyClosed,
        _ => StartAttemptResults.Unexpected,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "start dispatcher {DispatcherId} running (lease {LeaseSeconds}s)")]
    private static partial void LogStarted(ILogger logger, string dispatcherId, double leaseSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "start dispatcher disabled by configuration")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "start request of order {OrderId} closed: the order is already terminal (no workflow started)")]
    private static partial void LogClosedTerminal(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "start of order {OrderId}, attempt {Attempt}: {Result} (workflow host reported {Reported})")]
    private static partial void LogAttempt(ILogger logger, Guid orderId, int attempt, string result, string? reported);

    [LoggerMessage(Level = LogLevel.Warning, Message = "start of order {OrderId}, attempt {Attempt}: lease lost to another dispatcher; request left unchanged")]
    private static partial void LogLeaseLost(ILogger logger, Guid orderId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "start of order {OrderId}, attempt {Attempt}: the order became terminal meanwhile and its request is closed (workflow host reported {Reported}); nothing changed")]
    private static partial void LogAlreadyClosed(ILogger logger, Guid orderId, int attempt, string? reported);

    [LoggerMessage(Level = LogLevel.Error, Message = "start of order {OrderId}, attempt {Attempt}: the start request changed unexpectedly (no later claim, not closed; workflow host reported {Reported}); nothing changed")]
    private static partial void LogUnexpected(ILogger logger, Guid orderId, int attempt, string? reported);

    [LoggerMessage(Level = LogLevel.Warning, Message = "start of order {OrderId}, attempt {Attempt} failed; the lease will expire")]
    private static partial void LogAttemptFailed(ILogger logger, Guid orderId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "start dispatcher poll failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
