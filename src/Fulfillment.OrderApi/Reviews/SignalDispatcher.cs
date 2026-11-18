using System.Diagnostics.Metrics;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Fulfillment.OrderApi.Reviews;

public sealed class SignalDispatcherOptions
{
    public const string Section = "SignalDispatcher";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 10;

    public int MaxInFlight { get; set; } = 32;

    /// <summary>How long a claim is exclusive; a dispatcher that dies loses it after this time.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay after a refusal (4xx): a configuration problem, not a transient one.</summary>
    public TimeSpan RejectedBackoff { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Failpoint names of the signal dispatcher (drill builds only; key: signal id, N format).</summary>
public static class SignalFailpoints
{
    /// <summary>After the attempt is recorded, before the workflow host is called (drills hold it here).</summary>
    public const string BeforeSignalRaise = "BeforeSignalRaise";

    /// <summary>After the workflow host raised the event, before the signal is marked Forwarded (drills end the process here).</summary>
    public const string AfterSignalRaiseBeforeMarkForwarded = "AfterSignalRaiseBeforeMarkForwarded";
}

/// <summary>
/// Durable delivery of pending workflow signals (runs in every order-api replica). A signal is only a
/// wake-up hint; the fact it announces is already committed. Rules per claimed signal:
/// start request still Pending → retry later; instance running → raise, then Forwarded; instance
/// terminal → Discarded (WorkflowTerminal); instance missing although started → Stuck (for the
/// reconciler); no usable answer or not running → retry with backoff; refusal → long backoff. A
/// crash after the raise leaves the lease to expire and the signal is raised again, which is
/// harmless: the workflow reads the decision from SQL. Every change after the claim is fenced by the
/// lease token.
/// </summary>
public sealed partial class SignalDispatcher(
    IServiceScopeFactory scopes,
    SignalStore store,
    Failpoints failpoints,
    IOptions<SignalDispatcherOptions> options,
    RuntimeIdentity identity,
    ILogger<SignalDispatcher> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Fulfillment.OrderApi.Signals");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("workflow_signal_attempts_total", description: "signal delivery attempts by result");
    private static double oldestPendingSeconds;

    static SignalDispatcher()
    {
        Meter.CreateObservableGauge("pending_signals_oldest_age_seconds", () => oldestPendingSeconds,
            description: "age of the oldest pending signal (updated by each dispatcher poll)");
    }

    public string DispatcherId { get; } = $"{identity.Replica}/{Guid.NewGuid():N}"[..Math.Min(identity.Replica.Length + 33, 64)];

    /// <summary>One poll, awaited to the end. Returns the number of signals claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var started = await ClaimAndDispatchAsync(options.Value.BatchSize, ct);
        await Task.WhenAll(started);
        return started.Count;
    }

    /// <summary>The decision of one attempt from the workflow host's answer (pure; unit-tested).</summary>
    public static (string Result, string? State, string? ErrorCode) Decide(RaiseOutcome outcome, string startState)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            RaiseOutcomeKind.Raised => (SignalAttemptResults.Forwarded, "Forwarded", null),
            RaiseOutcomeKind.Terminal => (SignalAttemptResults.Discarded, "Discarded", SignalErrorCodes.WorkflowTerminal),
            RaiseOutcomeKind.NotFound when startState != "Pending" => (SignalAttemptResults.Stuck, "Stuck", SignalErrorCodes.WorkflowMissing),
            RaiseOutcomeKind.NotFound => (SignalAttemptResults.NotStarted, null, SignalErrorCodes.WorkflowNotStarted),
            RaiseOutcomeKind.NotRunning => (SignalAttemptResults.NotRunning, null, SignalErrorCodes.WorkflowNotRunning),
            RaiseOutcomeKind.Rejected => (SignalAttemptResults.Rejected, null, SignalErrorCodes.RaiseRejected),
            _ => (SignalAttemptResults.Ambiguous, null, SignalErrorCodes.RaiseAmbiguous),
        };
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
                    oldestPendingSeconds = await store.OldestPendingAgeAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    LogPollFailed(logger, e);
                }
            }

            if (claimedFull)
            {
                continue;
            }

            try
            {
                // Jittered for the same reason as the start dispatcher's poll: no replica wins every re-claim.
                await Task.Delay(settings.PollInterval * (0.5 + Random.Shared.NextDouble()), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await Task.WhenAll(inFlight);
    }

    private async Task<IReadOnlyList<Task>> ClaimAndDispatchAsync(int capacity, CancellationToken ct)
    {
        var settings = options.Value;
        var claimed = await store.ClaimDueAsync(DispatcherId, capacity, settings.Lease, ct);
        return [.. claimed.Select(claim => DispatchAsync(claim, settings, ct))];
    }

    private async Task DispatchAsync(ClaimedSignal claim, SignalDispatcherOptions settings, CancellationToken ct)
    {
        string result;
        RaiseOutcome? outcome = null;
        long attemptId = 0;
        var key = claim.SignalId.ToString("N");
        try
        {
            attemptId = await store.RecordAttemptAsync(claim, DispatcherId, ct);
            if (claim.StartState == "Pending")
            {
                // The workflow does not exist yet; its first wake-up reads the decision anyway.
                result = Fenced(await store.ReleaseForRetryAsync(claim, SignalErrorCodes.WorkflowNotStarted, Backoff(claim.Attempt, settings), ct),
                    SignalAttemptResults.NotStarted);
            }
            else
            {
                await failpoints.HitAsync(SignalFailpoints.BeforeSignalRaise, key, ct);
                await using var scope = scopes.CreateAsyncScope();
                var signaler = scope.ServiceProvider.GetRequiredService<IWorkflowSignaler>();
                outcome = await signaler.RaiseAsync(claim.InstanceId, claim.EventName, new ReviewSignal(claim.SignalId), claim.SignalId, ct);
                await failpoints.HitAsync(SignalFailpoints.AfterSignalRaiseBeforeMarkForwarded, key, ct);
                var (decided, state, code) = Decide(outcome, claim.StartState);
                var update = state switch
                {
                    "Forwarded" => await store.MarkForwardedAsync(claim, ct),
                    "Discarded" or "Stuck" => await store.CloseAsync(claim, decided, code!, ct),
                    _ => await store.ReleaseForRetryAsync(claim, code!,
                        decided == SignalAttemptResults.Rejected ? settings.RejectedBackoff : Backoff(claim.Attempt, settings), ct),
                };
                result = Fenced(update, decided);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            // Includes SQL failures after the raise: the lease expires and the signal is raised again.
            LogAttemptFailed(logger, claim.SignalId, claim.Attempt, e);
            result = SignalAttemptResults.Ambiguous;
        }

        Attempts.Add(1, new KeyValuePair<string, object?>("result", result));
        LogAttempt(logger, claim.SignalId, claim.EventName, claim.Attempt, result, outcome?.ReportedStatus, outcome?.HttpStatus, outcome?.InvocationAttempts, outcome?.TransportError);
        if (attemptId != 0)
        {
            try
            {
                await store.CompleteAttemptAsync(attemptId, result, outcome, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogAttemptFailed(logger, claim.SignalId, claim.Attempt, e);
            }
        }
    }

    private static string Fenced(FencedUpdateOutcome update, string applied) => update switch
    {
        FencedUpdateOutcome.Applied => applied,
        FencedUpdateOutcome.LeaseLost => SignalAttemptResults.LeaseLost,
        _ => SignalAttemptResults.Unexpected,
    };

    private static TimeSpan Backoff(int attempt, SignalDispatcherOptions settings) =>
        WorkflowStartDispatcher.Backoff(attempt, settings.BaseBackoff, settings.MaxBackoff, Random.Shared.NextDouble());

    [LoggerMessage(Level = LogLevel.Information, Message = "signal dispatcher {DispatcherId} running (lease {LeaseSeconds}s)")]
    private static partial void LogStarted(ILogger logger, string dispatcherId, double leaseSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "signal dispatcher disabled by configuration")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "signal {SignalId} ({EventName}), attempt {Attempt}: {Result} (workflow host reported {Reported}; http {Http}; invocation attempts {Calls}; transport {Transport})")]
    private static partial void LogAttempt(ILogger logger, Guid signalId, string eventName, int attempt, string result, string? reported, int? http, int? calls, string? transport);

    [LoggerMessage(Level = LogLevel.Warning, Message = "signal {SignalId}, attempt {Attempt} failed; the lease will expire")]
    private static partial void LogAttemptFailed(ILogger logger, Guid signalId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "signal dispatcher poll failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
