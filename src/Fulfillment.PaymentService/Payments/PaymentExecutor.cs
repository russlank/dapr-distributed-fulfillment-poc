using Fulfillment.Contracts.Payments;
using Fulfillment.Contracts.Workflows;
using Microsoft.Data.SqlClient;

namespace Fulfillment.PaymentService.Payments;

/// <summary>The payment service's budget for one operation (drill environment: shorter).</summary>
public sealed class PaymentOptions
{
    public const string Section = "Payment";

    public Uri ProviderUrl { get; set; } = new("http://payment-provider-simulator:8080");

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Requests per operation and execution, including resends after a NotFound.</summary>
    public int MaxSends { get; set; } = 4;

    public TimeSpan SendBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public int StatusQueries { get; set; } = 6;

    public TimeSpan StatusQueryInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A NotFound is trusted (the request did not commit and will not) only this long after the last possible send.</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromSeconds(10);

    public static void ForEnvironment(PaymentOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.RequestTimeout = TimeSpan.FromSeconds(2);
            options.SendBackoff = TimeSpan.FromMilliseconds(500);
            options.StatusQueryInterval = TimeSpan.FromMilliseconds(500);
            options.IdempotencyWindow = TimeSpan.FromSeconds(3);
        }
    }
}

/// <summary>
/// Runs one payment operation by its operation key, effectively once:
/// <list type="number">
/// <item>The intent row is written before any request; an operation already finished returns its stored outcome.</item>
/// <item>Every request is recorded before it is sent. A request without an answer, a timeout, or a broken
/// connection may have committed at the provider: the next step is a status query, never a blind resend.</item>
/// <item>Status query: Committed or Prohibited is adopted; NotFound is trusted only after the provider's
/// idempotency window since the last possible send, and then the same key is sent again; Unknown or an
/// unreachable provider is retried within the budget.</item>
/// <item>Out of budget: Unavailable when no request can have reached the provider, otherwise Ambiguous.</item>
/// </list>
/// The outcome is recorded once (conditional update); an execution that loses that race returns the stored one,
/// which agrees, because the provider answers every request of a key with the same result. A new key is never
/// used to try again.
/// </summary>
public sealed partial class PaymentExecutor(string connectionString, ProviderClient provider, PaymentOptions options, string replica, ILogger<PaymentExecutor> logger)
{
    public async Task<PaymentOperationResult> ExecuteAsync(PaymentOperationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = PaymentSteps.Key(request.OrderId, request.Step, request.Qualifier);
        var reference = request.ReferenceStep is null ? null : PaymentSteps.Key(request.OrderId, request.ReferenceStep, request.ReferenceQualifier ?? PaymentSteps.First);
        var providerRequest = new ProviderOperationRequest(key, KindOf(request.Step), request.AmountMinor, request.Currency, reference);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await EnsureIntentAsync(connection, request, providerRequest, ct);
        if (request.RetryUnresolved)
        {
            // Unresolved outcomes are not final for a compensation: run again (same key) with a fresh budget.
            await ExecAsync(connection,
                """
                UPDATE payment.PaymentOperation SET State = 'Pending', Outcome = NULL, ResultCode = NULL, FinishedAt = NULL, FinishedBy = NULL
                WHERE OperationKey = @k AND State = 'Finished' AND Outcome IN ('Unavailable', 'Ambiguous');
                """, ct, ("@k", key));
        }

        if (await ReadFinishedAsync(connection, key, ct) is { } stored)
        {
            await ExecAsync(connection, "UPDATE payment.PaymentOperation SET Replays = Replays + 1 WHERE OperationKey = @k", ct, ("@k", key));
            return stored with { Replayed = true };
        }

        // Earlier executions (a crash, a retried activity) may have sent a request whose answer is unknown.
        var maySent = await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM payment.PaymentAttempt WHERE OperationKey = @k AND Kind = 'Send' AND (Answer IS NULL OR Answer IN ('Timeout', 'Error'))",
            ct, ("@k", key)) > 0;
        var everSent = maySent;
        var sends = 0;
        while (true)
        {
            if (maySent)
            {
                var (status, response) = await ReconcileAsync(connection, key, ct);
                if (status is ProviderAnswers.Committed or ProviderAnswers.Prohibited)
                {
                    return await FinishAsync(connection, key, response!, ct);
                }

                if (status != ProviderAnswers.NotFound)
                {
                    return await FinishAsync(connection, key, PaymentOutcomes.Ambiguous, "StatusUndetermined", null, ct);
                }

                // NotFound after the idempotency window: the request did not commit; send the same key again.
                maySent = false;
            }

            if (sends >= options.MaxSends)
            {
                return everSent
                    ? await FinishAsync(connection, key, PaymentOutcomes.Ambiguous, "BudgetExhausted", null, ct)
                    : await FinishAsync(connection, key, PaymentOutcomes.Unavailable, "ProviderUnreachable", null, ct);
            }

            if (sends > 0)
            {
                await Task.Delay(Backoff(sends), ct);
            }

            sends++;
            var attempt = await StartAttemptAsync(connection, key, "Send", ct);
            var answer = await provider.SendAsync(providerRequest, options.RequestTimeout, ct);
            await EndAttemptAsync(connection, attempt, answer, ct);
            LogSent(logger, key, sends, answer.Answer, answer.Detail);
            switch (answer.Answer)
            {
                case ProviderAnswers.Committed:
                case ProviderAnswers.Prohibited:
                    return await FinishAsync(connection, key, answer.Response!, ct);
                case ProviderAnswers.Conflict:
                    // Same key, other payload: a defect; never retried with another key.
                    return await FinishAsync(connection, key, PaymentOutcomes.Ambiguous, "PayloadConflict", null, ct);
                case var a when ProviderAnswers.MayHaveReached(a):
                    maySent = true;
                    everSent = true;
                    break;
            }
        }
    }

    /// <summary>
    /// For an operation finished Ambiguous: status queries with a fresh budget; a Committed or Prohibited answer
    /// replaces the Ambiguous outcome (conditional update). Returns the stored outcome afterwards. Sends nothing.
    /// </summary>
    public async Task<PaymentOperationResult> ReconcileFinishedAsync(PaymentOperationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = PaymentSteps.Key(request.OrderId, request.Step, request.Qualifier);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var stored = await ReadFinishedAsync(connection, key, ct)
            ?? throw new InvalidOperationException($"payment operation {key} has not finished");
        if (stored.Outcome != PaymentOutcomes.Ambiguous)
        {
            return stored;
        }

        var (status, response) = await ReconcileAsync(connection, key, ct);
        if (status is ProviderAnswers.Committed or ProviderAnswers.Prohibited)
        {
            var outcome = OutcomeOf(response!);
            var code = response!.Status == ProviderStatuses.Prohibited ? "ProhibitedBy:" + response.ProhibitedBy : response.ResultCode;
            await using var update = new SqlCommand(
                """
                UPDATE payment.PaymentOperation SET Outcome = @o, ResultCode = @c, AuthorizationExpiresAt = @e, FinishedBy = @r
                WHERE OperationKey = @k AND State = 'Finished' AND Outcome = 'Ambiguous';
                """, connection);
            update.Parameters.AddWithValue("@k", key);
            update.Parameters.AddWithValue("@o", outcome);
            update.Parameters.AddWithValue("@c", (object?)code ?? DBNull.Value);
            update.Parameters.Add("@e", System.Data.SqlDbType.DateTime2).Value = (object?)response.AuthorizationExpiresAtUtc ?? DBNull.Value;
            update.Parameters.AddWithValue("@r", "reconcile:" + replica);
            await update.ExecuteNonQueryAsync(ct);
            LogFinished(logger, key, outcome, "reconciled");
        }

        return (await ReadFinishedAsync(connection, key, ct))!;
    }

    /// <summary>Status queries until the outcome is known, or the budget is used up (Unknown, unreachable, or a NotFound still inside the idempotency window).</summary>
    private async Task<(string Status, ProviderOperationResponse? Response)> ReconcileAsync(SqlConnection connection, string key, CancellationToken ct)
    {
        for (var i = 0; i < options.StatusQueries; i++)
        {
            if (i > 0)
            {
                await Task.Delay(options.StatusQueryInterval, ct);
            }

            var attempt = await StartAttemptAsync(connection, key, "Status", ct);
            var answer = await provider.QueryAsync(key, options.RequestTimeout, ct);
            await EndAttemptAsync(connection, attempt, answer, ct);
            LogQueried(logger, key, i + 1, answer.Answer);
            switch (answer.Answer)
            {
                case ProviderAnswers.Committed:
                case ProviderAnswers.Prohibited:
                    return (answer.Answer, answer.Response);
                case ProviderAnswers.NotFound:
                    var sinceLastSend = await ScalarAsync<int>(connection,
                        "SELECT ISNULL(DATEDIFF(millisecond, MAX(StartedAt), SYSUTCDATETIME()), 2147483647) FROM payment.PaymentAttempt WHERE OperationKey = @k AND Kind = 'Send'",
                        ct, ("@k", key));
                    if (sinceLastSend >= options.IdempotencyWindow.TotalMilliseconds)
                    {
                        return (ProviderAnswers.NotFound, null);
                    }

                    break;
            }
        }

        return (ProviderAnswers.Unknown, null);
    }

    private TimeSpan Backoff(int sends) => TimeSpan.FromMilliseconds(Math.Min(options.SendBackoff.TotalMilliseconds * Math.Pow(2, sends - 1), 8_000));

    private static string KindOf(string step) => step switch
    {
        PaymentSteps.Authorize => ProviderKinds.Authorize,
        PaymentSteps.Capture => ProviderKinds.Capture,
        PaymentSteps.Void => ProviderKinds.Void,
        PaymentSteps.Refund => ProviderKinds.Refund,
        _ => throw new ArgumentException($"unknown payment step {step}", nameof(step)),
    };

    private static string OutcomeOf(ProviderOperationResponse r) => r.Status == ProviderStatuses.Prohibited
        ? PaymentOutcomes.Prohibited
        : r.Result switch
        {
            ProviderResults.Authorized => PaymentOutcomes.Authorized,
            ProviderResults.Declined => PaymentOutcomes.Declined,
            ProviderResults.Captured => PaymentOutcomes.Captured,
            ProviderResults.Voided => PaymentOutcomes.Voided,
            ProviderResults.Refunded => PaymentOutcomes.Refunded,
            ProviderResults.NothingToVoid or ProviderResults.NothingToRefund => PaymentOutcomes.NothingToCompensate,
            ProviderResults.Rejected => PaymentOutcomes.Rejected,
            _ => throw new InvalidOperationException($"unexpected provider result {r.Result}"),
        };

    private Task<PaymentOperationResult> FinishAsync(SqlConnection connection, string key, ProviderOperationResponse response, CancellationToken ct) =>
        FinishAsync(connection, key, OutcomeOf(response), response.Status == ProviderStatuses.Prohibited ? "ProhibitedBy:" + response.ProhibitedBy : response.ResultCode,
            response.AuthorizationExpiresAtUtc, ct);

    /// <summary>Records the outcome once; whoever finished first wins and everyone returns the stored outcome.</summary>
    private async Task<PaymentOperationResult> FinishAsync(SqlConnection connection, string key, string outcome, string? code, DateTime? expiresAt, CancellationToken ct)
    {
        await using (var update = new SqlCommand(
                         """
                         UPDATE payment.PaymentOperation
                         SET State = 'Finished', Outcome = @o, ResultCode = @c, AuthorizationExpiresAt = @e, FinishedAt = SYSUTCDATETIME(), FinishedBy = @r
                         WHERE OperationKey = @k AND State = 'Pending';
                         """, connection))
        {
            update.Parameters.AddWithValue("@k", key);
            update.Parameters.AddWithValue("@o", outcome);
            update.Parameters.AddWithValue("@c", (object?)code ?? DBNull.Value);
            update.Parameters.Add("@e", System.Data.SqlDbType.DateTime2).Value = (object?)expiresAt ?? DBNull.Value;
            update.Parameters.AddWithValue("@r", replica);
            await update.ExecuteNonQueryAsync(ct);
        }

        LogFinished(logger, key, outcome, code);
        return (await ReadFinishedAsync(connection, key, ct))!;
    }

    private static async Task EnsureIntentAsync(SqlConnection connection, PaymentOperationRequest request, ProviderOperationRequest p, CancellationToken ct)
    {
        await using var insert = new SqlCommand(
            """
            INSERT payment.PaymentOperation (OperationKey, OrderId, Kind, AmountMinor, Currency, Reference, State, Replays)
            SELECT @k, @o, @kind, @a, @c, @ref, 'Pending', 0
            WHERE NOT EXISTS (SELECT 1 FROM payment.PaymentOperation WITH (UPDLOCK, HOLDLOCK) WHERE OperationKey = @k);
            """, connection);
        insert.Parameters.AddWithValue("@k", p.OperationKey);
        insert.Parameters.AddWithValue("@o", request.OrderId);
        insert.Parameters.AddWithValue("@kind", p.Kind);
        insert.Parameters.AddWithValue("@a", p.AmountMinor);
        insert.Parameters.AddWithValue("@c", p.Currency);
        insert.Parameters.AddWithValue("@ref", (object?)p.Reference ?? DBNull.Value);
        try
        {
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException e) when (e.Number is 2627 or 2601)
        {
            // Inserted concurrently by another execution of the same key.
        }
    }

    private async Task<PaymentOperationResult?> ReadFinishedAsync(SqlConnection connection, string key, CancellationToken ct)
    {
        await using var read = new SqlCommand(
            """
            SELECT Outcome, ResultCode, AuthorizationExpiresAt,
                   (SELECT COUNT(*) FROM payment.PaymentAttempt a WHERE a.OperationKey = o.OperationKey AND a.Kind = 'Send'),
                   (SELECT COUNT(*) FROM payment.PaymentAttempt a WHERE a.OperationKey = o.OperationKey AND a.Kind = 'Status')
            FROM payment.PaymentOperation o WHERE OperationKey = @k AND State = 'Finished';
            """, connection);
        read.Parameters.AddWithValue("@k", key);
        await using var reader = await read.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PaymentOperationResult(key, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc), reader.GetInt32(3), reader.GetInt32(4), false, replica)
            : null;
    }

    private async Task<long> StartAttemptAsync(SqlConnection connection, string key, string kind, CancellationToken ct)
    {
        await using var insert = new SqlCommand("INSERT payment.PaymentAttempt (OperationKey, Kind, Replica) OUTPUT inserted.Id VALUES (@k, @kind, @r)", connection);
        insert.Parameters.AddWithValue("@k", key);
        insert.Parameters.AddWithValue("@kind", kind);
        insert.Parameters.AddWithValue("@r", replica);
        return (long)(await insert.ExecuteScalarAsync(ct))!;
    }

    private static async Task EndAttemptAsync(SqlConnection connection, long id, ProviderAnswer answer, CancellationToken ct)
    {
        await using var update = new SqlCommand("UPDATE payment.PaymentAttempt SET Answer = @a, HttpStatus = @s, Detail = @d, EndedAt = SYSUTCDATETIME() WHERE Id = @id", connection);
        update.Parameters.AddWithValue("@id", id);
        update.Parameters.AddWithValue("@a", answer.Answer);
        update.Parameters.Add("@s", System.Data.SqlDbType.Int).Value = (object?)answer.HttpStatus ?? DBNull.Value;
        var detail = answer.Detail is { Length: > 400 } d ? d[..400] : answer.Detail;
        update.Parameters.AddWithValue("@d", (object?)detail ?? DBNull.Value);
        await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "payment {OperationKey}: send {Attempt} → {Answer} ({Detail})")]
    private static partial void LogSent(ILogger logger, string operationKey, int attempt, string answer, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "payment {OperationKey}: status query {Query} → {Answer}")]
    private static partial void LogQueried(ILogger logger, string operationKey, int query, string answer);

    [LoggerMessage(Level = LogLevel.Information, Message = "payment {OperationKey}: finished {Outcome} ({Code})")]
    private static partial void LogFinished(ILogger logger, string operationKey, string outcome, string? code);
}
