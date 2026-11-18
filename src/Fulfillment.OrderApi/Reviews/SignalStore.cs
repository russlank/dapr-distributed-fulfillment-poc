using System.Net;
using System.Text.Json;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Messaging;
using Fulfillment.OrderApi.Dispatch;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Reviews;

/// <summary>A pending signal claimed by one dispatcher; every later change must present the token.</summary>
/// <param name="StartState">State of the order's start request when claimed (Pending, Started, Closed).</param>
public sealed record ClaimedSignal(Guid SignalId, Guid OrderId, string InstanceId, string EventName, int Attempt, Guid LeaseToken, string StartState);

/// <summary>Result codes of signal attempts (closed code domain, matches CK_SignalAttempt_Result).</summary>
public static class SignalAttemptResults
{
    /// <summary>The workflow host raised the event to a running instance: the signal is Forwarded.</summary>
    public const string Forwarded = "Forwarded";

    /// <summary>The instance is terminal: the signal is Discarded (the fact stays in SQL).</summary>
    public const string Discarded = "Discarded";

    /// <summary>The instance is missing although started: Stuck, for the reconciler.</summary>
    public const string Stuck = "Stuck";

    /// <summary>The order's workflow was not started yet: retried later.</summary>
    public const string NotStarted = "NotStarted";

    /// <summary>The instance exists but is not running (for example pending or suspended): retried later.</summary>
    public const string NotRunning = "NotRunning";

    /// <summary>No usable answer (transport errors or 5xx after the bounded retries): retried later.</summary>
    public const string Ambiguous = "Ambiguous";

    /// <summary>The workflow host refused the call (4xx): retried after a long backoff.</summary>
    public const string Rejected = "Rejected";

    public const string LeaseLost = "LeaseLost";
    public const string Unexpected = "Unexpected";
}

/// <summary>Error codes stored on a signal (closed code domain).</summary>
public static class SignalErrorCodes
{
    public const string WorkflowTerminal = "WorkflowTerminal";
    public const string WorkflowMissing = "WorkflowMissing";
    public const string WorkflowNotStarted = "WorkflowNotStarted";
    public const string WorkflowNotRunning = "WorkflowNotRunning";
    public const string RaiseAmbiguous = "RaiseAmbiguous";
    public const string RaiseRejected = "RaiseRejected";
}

public enum RaiseOutcomeKind
{
    Raised,
    NotRunning,
    Terminal,
    NotFound,
    Ambiguous,
    Rejected,
}

/// <summary>What the workflow host answered to a raise, with the invocation's diagnostics.</summary>
public sealed record RaiseOutcome(RaiseOutcomeKind Kind, string? ReportedStatus, int InvocationAttempts, int? HttpStatus, string? TransportError);

/// <summary>Raises workflow events; the boundary the dispatcher's tests replace.</summary>
public interface IWorkflowSignaler
{
    Task<RaiseOutcome> RaiseAsync(string instanceId, string eventName, ReviewSignal payload, Guid signalId, CancellationToken ct);
}

/// <summary>
/// Raises through the workflow host's management API (service invocation with bounded retries and
/// one idempotency key per signal), never through the order API's own sidecar. The API raises only
/// to a running instance and otherwise returns its state.
/// </summary>
public sealed class WorkflowHostSignaler(SidecarInvoker invoker) : IWorkflowSignaler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> TerminalStatuses = new(["Completed", "Failed", "Terminated", "Canceled"], StringComparer.Ordinal);

    public async Task<RaiseOutcome> RaiseAsync(string instanceId, string eventName, ReviewSignal payload, Guid signalId, CancellationToken ct)
    {
        var result = await invoker.PostAsync(AppIds.WorkflowHost, WorkflowManagementRoutes.Event(instanceId, eventName), payload, $"signal:{signalId:N}", ct);
        var http = result.Status is { } s ? (int)s : (int?)null;
        // Diagnostics: the transport error, or for an error response the start of its body (the sidecar's error code and message).
        var detail = result.TransportError ?? (result.IsSuccess ? null : result.Body);
        var error = detail is { Length: > 200 } e ? e[..200] : detail;
        if (result.IsSuccess)
        {
            var answer = JsonSerializer.Deserialize<WorkflowEventResult>(result.Body, Json);
            if (answer is null)
            {
                return new RaiseOutcome(RaiseOutcomeKind.Ambiguous, null, result.Attempts, http, error);
            }

            var kind = answer.Raised ? RaiseOutcomeKind.Raised
                : answer.Status == "NotFound" ? RaiseOutcomeKind.NotFound
                : TerminalStatuses.Contains(answer.Status) ? RaiseOutcomeKind.Terminal
                : RaiseOutcomeKind.NotRunning;
            return new RaiseOutcome(kind, answer.Status, result.Attempts, http, error);
        }

        return result.Status is { } status && (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout
            ? new RaiseOutcome(RaiseOutcomeKind.Rejected, http?.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Attempts, http, error)
            : new RaiseOutcome(RaiseOutcomeKind.Ambiguous, http?.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Attempts, http, error);
    }
}

/// <summary>
/// SQL coordination of the signal dispatchers, like the start dispatchers': due Pending signals are
/// claimed with UPDLOCK and READPAST and a new lease token; every later change is conditional on the
/// token. A signal is marked Forwarded only after the workflow host raised it to a running instance.
/// </summary>
public sealed class SignalStore(string connectionString)
{
    public async Task<IReadOnlyList<ClaimedSignal>> ClaimDueAsync(string dispatcherId, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @due TABLE (SignalId uniqueidentifier PRIMARY KEY, StartState varchar(16) NOT NULL);
            INSERT @due (SignalId, StartState)
            SELECT TOP (@batch) s.SignalId, ISNULL(r.State, 'Pending')
            FROM orders.PendingSignal s WITH (UPDLOCK, READPAST, ROWLOCK)
            LEFT JOIN orders.WorkflowStartRequest r WITH (READCOMMITTEDLOCK) ON r.OrderId = s.OrderId
            WHERE s.State = 'Pending' AND s.NextAttemptAt <= SYSUTCDATETIME()
              AND (s.LeaseExpiresAt IS NULL OR s.LeaseExpiresAt <= SYSUTCDATETIME())
            ORDER BY s.NextAttemptAt;

            UPDATE s SET LeaseOwner = @owner, LeaseToken = NEWID(), Attempts = Attempts + 1,
                         LeaseExpiresAt = DATEADD(millisecond, @leaseMs, SYSUTCDATETIME())
            OUTPUT inserted.SignalId, inserted.OrderId, inserted.InstanceId, inserted.EventName, inserted.Attempts, inserted.LeaseToken, d.StartState
            FROM orders.PendingSignal s JOIN @due d ON d.SignalId = s.SignalId;
            COMMIT;
            """, connection);
        command.Parameters.AddWithValue("@batch", batchSize);
        command.Parameters.AddWithValue("@owner", dispatcherId);
        command.Parameters.AddWithValue("@leaseMs", (int)lease.TotalMilliseconds);
        var claimed = new List<ClaimedSignal>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            claimed.Add(new ClaimedSignal(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetGuid(5), reader.GetString(6)));
        }

        return claimed;
    }

    /// <summary>Age of the oldest Pending signal in seconds (0 when none), by SQL time.</summary>
    public async Task<double> OldestPendingAgeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT ISNULL(DATEDIFF_BIG(millisecond, MIN(CreatedAt), SYSUTCDATETIME()), 0) FROM orders.PendingSignal WHERE State = 'Pending';", connection);
        return Convert.ToDouble(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) / 1000;
    }

    public async Task<long> RecordAttemptAsync(ClaimedSignal claim, string dispatcherId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            INSERT orders.SignalAttempt (SignalId, LeaseToken, DispatcherId, Attempt)
            OUTPUT inserted.Id VALUES (@s, @t, @d, @a);
            """, connection);
        command.Parameters.AddWithValue("@s", claim.SignalId);
        command.Parameters.AddWithValue("@t", claim.LeaseToken);
        command.Parameters.AddWithValue("@d", dispatcherId);
        command.Parameters.AddWithValue("@a", claim.Attempt);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task CompleteAttemptAsync(long attemptId, string result, RaiseOutcome? outcome, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            UPDATE orders.SignalAttempt
            SET CompletedAt = SYSUTCDATETIME(), Result = @r, ReportedStatus = @s, InvocationAttempts = @n, HttpStatus = @h, TransportError = @e
            WHERE Id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", attemptId);
        command.Parameters.AddWithValue("@r", result);
        command.Parameters.AddWithValue("@s", (object?)outcome?.ReportedStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@n", (object?)outcome?.InvocationAttempts ?? DBNull.Value);
        command.Parameters.AddWithValue("@h", (object?)outcome?.HttpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@e", (object?)outcome?.TransportError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<FencedUpdateOutcome> MarkForwardedAsync(ClaimedSignal claim, CancellationToken ct) =>
        ExecuteFencedAsync(
            """
            UPDATE orders.PendingSignal SET State = 'Forwarded', ForwardedAt = SYSUTCDATETIME(), LastErrorCode = NULL,
                   LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
            WHERE SignalId = @s AND LeaseToken = @t AND State = 'Pending';
            """, claim, null, TimeSpan.Zero, ct);

    /// <summary>Discarded (terminal instance) or Stuck (missing instance); both are final for the dispatcher.</summary>
    public Task<FencedUpdateOutcome> CloseAsync(ClaimedSignal claim, string state, string errorCode, CancellationToken ct) =>
        ExecuteFencedAsync(
            $"""
             UPDATE orders.PendingSignal SET State = '{(state == SignalAttemptResults.Stuck ? "Stuck" : "Discarded")}', ClosedAt = SYSUTCDATETIME(), LastErrorCode = @code,
                    LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
             WHERE SignalId = @s AND LeaseToken = @t AND State = 'Pending';
             """, claim, errorCode, TimeSpan.Zero, ct);

    public Task<FencedUpdateOutcome> ReleaseForRetryAsync(ClaimedSignal claim, string errorCode, TimeSpan backoff, CancellationToken ct) =>
        ExecuteFencedAsync(
            """
            UPDATE orders.PendingSignal SET LastErrorCode = @code, NextAttemptAt = DATEADD(millisecond, @backoffMs, SYSUTCDATETIME()),
                   LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
            WHERE SignalId = @s AND LeaseToken = @t AND State = 'Pending';
            """, claim, errorCode, backoff, ct);

    private async Task<FencedUpdateOutcome> ExecuteFencedAsync(string sql, ClaimedSignal claim, string? code, TimeSpan backoff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            sql + """

            IF @@ROWCOUNT = 1 SELECT CAST(1 AS bit), NULL, 0;
            ELSE SELECT CAST(0 AS bit), s.State, s.Attempts FROM (SELECT 1 AS One) x
                 LEFT JOIN orders.PendingSignal s ON s.SignalId = @s;
            """, connection);
        command.Parameters.AddWithValue("@s", claim.SignalId);
        command.Parameters.AddWithValue("@t", claim.LeaseToken);
        command.Parameters.AddWithValue("@code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("@backoffMs", (int)backoff.TotalMilliseconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetBoolean(0))
        {
            return FencedUpdateOutcome.Applied;
        }

        // A later claim means the lease was lost; nothing else removes a signal's token.
        var attempts = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        return !reader.IsDBNull(1) && attempts > claim.Attempt ? FencedUpdateOutcome.LeaseLost : FencedUpdateOutcome.Unexpected;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
