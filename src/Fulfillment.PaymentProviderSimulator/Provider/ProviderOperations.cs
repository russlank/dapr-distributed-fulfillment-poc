using System.Security.Cryptography;
using System.Text;
using Fulfillment.Contracts.Payments;
using Microsoft.Data.SqlClient;

namespace Fulfillment.PaymentProviderSimulator.Provider;

/// <summary>Provider business settings (drill environment: short authorization validity).</summary>
public sealed class ProviderOptions
{
    public const string Section = "Provider";

    public TimeSpan AuthorizationValidity { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Authorizations from this amount (minor units) are declined; null: never by amount.</summary>
    public long? DeclineFromMinor { get; set; } = 10_000_000;

    public static void ForEnvironment(ProviderOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.AuthorizationValidity = TimeSpan.FromSeconds(120);
        }
    }
}

/// <summary>What happened to a request: the answer, and a fault to apply after the commit (if any).</summary>
public sealed record ProviderOutcome(ProviderOperationResponse? Response, ProviderOutcomeKind Kind, string? PostCommitFault = null, int? FaultParam = null);

public enum ProviderOutcomeKind
{
    Ok,
    PayloadConflict,
    RejectedBeforeCommit,
}

/// <summary>
/// The provider's operations as guarded SQL. A request locks its own key and the key it references (sorted, so
/// two requests never wait on each other in opposite order); everything that decides the result runs in one
/// transaction: the stored result of a repeated key, a prohibition left by an earlier compensation, the state of
/// the referenced operation, and the insert of the new operation (or of a prohibition).
/// </summary>
public sealed class ProviderOperations(string connectionString, ProviderOptions options)
{
    private const int DeadlockRetries = 5;
    private static readonly string[] PostCommitFaults = ["DropResponseAfterCommit", "DelayResponse", "CrashAfterCommit"];

    public async Task<ProviderOutcome> ExecuteAsync(ProviderOperationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteOnceAsync(request, ct);
            }
            catch (SqlException e) when (e.Number == 1205 && attempt < DeadlockRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }
    }

    private async Task<ProviderOutcome> ExecuteOnceAsync(ProviderOperationRequest r, CancellationToken ct)
    {
        var hash = PayloadHash(r);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var key in new[] { r.OperationKey, r.Reference }.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await LockKeyAsync(connection, tx, key, ct);
        }

        var existing = await ReadOperationAsync(connection, tx, r.OperationKey, ct);
        if (existing is not null)
        {
            if (!existing.Hash.AsSpan().SequenceEqual(hash))
            {
                await tx.RollbackAsync(ct);
                return new ProviderOutcome(null, ProviderOutcomeKind.PayloadConflict);
            }

            await ExecAsync(connection, tx, "UPDATE provider.Operation SET Replays = Replays + 1 WHERE OperationKey = @k", ct, ("@k", r.OperationKey));
            await tx.CommitAsync(ct);
            return new ProviderOutcome(existing.Response with { Replayed = true }, ProviderOutcomeKind.Ok);
        }

        var prohibitedBy = await ScalarAsync<string>(connection, tx, "SELECT ByOperationKey FROM provider.Prohibition WHERE OperationKey = @k", ct, ("@k", r.OperationKey));
        if (prohibitedBy is not null)
        {
            await tx.CommitAsync(ct);
            return new ProviderOutcome(new ProviderOperationResponse(r.OperationKey, ProviderStatuses.Prohibited, r.Kind, ProhibitedBy: prohibitedBy), ProviderOutcomeKind.Ok);
        }

        var faults = await TakeFaultsAsync(connection, tx, r, ct);
        if (faults.TryGetValue("RejectBeforeCommit", out _))
        {
            await tx.CommitAsync(ct);
            return new ProviderOutcome(null, ProviderOutcomeKind.RejectedBeforeCommit);
        }

        var (result, code, expiresInSeconds) = await DecideAsync(connection, tx, r, faults, ct);
        var commitFault = PostCommitFaults.FirstOrDefault(faults.ContainsKey);
        var marker = string.Join(",", faults.Keys.Where(k => k != "StatusUnknown").Order(StringComparer.Ordinal));
        await using (var insert = new SqlCommand(
                         """
                         INSERT provider.Operation (OperationKey, Kind, PayloadHash, AmountMinor, Currency, Reference, Result, ResultCode, AuthorizationExpiresAt, FaultApplied, Replays)
                         VALUES (@k, @kind, @h, @a, @c, @ref, @res, @code,
                                 CASE WHEN @exp IS NULL THEN NULL ELSE DATEADD(second, @exp, SYSUTCDATETIME()) END,
                                 NULLIF(@fault, ''), 0);
                         """, connection, tx))
        {
            insert.Parameters.AddWithValue("@k", r.OperationKey);
            insert.Parameters.AddWithValue("@kind", r.Kind);
            insert.Parameters.AddWithValue("@h", hash);
            insert.Parameters.AddWithValue("@a", r.AmountMinor);
            insert.Parameters.AddWithValue("@c", r.Currency);
            insert.Parameters.AddWithValue("@ref", (object?)r.Reference ?? DBNull.Value);
            insert.Parameters.AddWithValue("@res", result);
            insert.Parameters.AddWithValue("@code", (object?)code ?? DBNull.Value);
            insert.Parameters.Add("@exp", System.Data.SqlDbType.Int).Value = (object?)expiresInSeconds ?? DBNull.Value;
            insert.Parameters.AddWithValue("@fault", marker);
            await insert.ExecuteNonQueryAsync(ct);
        }

        // Compensation of an operation that never committed: it can no longer commit (OD-19).
        if (result is ProviderResults.NothingToVoid or ProviderResults.NothingToRefund && code == ProviderResultCodes.ReferenceNeverCommitted)
        {
            await ExecAsync(connection, tx,
                "INSERT provider.Prohibition (OperationKey, ByOperationKey) SELECT @ref, @k WHERE NOT EXISTS (SELECT 1 FROM provider.Prohibition WHERE OperationKey = @ref)",
                ct, ("@ref", r.Reference!), ("@k", r.OperationKey));
        }

        var stored = (await ReadOperationAsync(connection, tx, r.OperationKey, ct))!;
        await tx.CommitAsync(ct);
        return new ProviderOutcome(stored.Response, ProviderOutcomeKind.Ok, commitFault, commitFault is null ? null : faults[commitFault]);
    }

    /// <summary>The business rules. Returns the result, its code, and (authorizations) the validity in seconds.</summary>
    private async Task<(string Result, string? Code, int? ExpiresInSeconds)> DecideAsync(
        SqlConnection connection, SqlTransaction tx, ProviderOperationRequest r, Dictionary<string, int?> faults, CancellationToken ct)
    {
        switch (r.Kind)
        {
            case ProviderKinds.Authorize:
                if (faults.ContainsKey("Decline") || (options.DeclineFromMinor is { } limit && r.AmountMinor >= limit))
                {
                    return (ProviderResults.Declined, null, null);
                }

                var validity = faults.TryGetValue("ExpireAuthorization", out var seconds) ? seconds ?? 0 : (int)options.AuthorizationValidity.TotalSeconds;
                return (ProviderResults.Authorized, null, validity);

            case ProviderKinds.Capture:
                {
                    var auth = await ReadOperationAsync(connection, tx, r.Reference!, ct);
                    if (auth is null || auth.Response.Kind != ProviderKinds.Authorize)
                    {
                        return (ProviderResults.Rejected, ProviderResultCodes.AuthorizationNotFound, null);
                    }

                    if (await EffectExistsAsync(connection, tx, r.Reference!, ProviderKinds.Void, ct))
                    {
                        return (ProviderResults.Rejected, ProviderResultCodes.AuthorizationVoided, null);
                    }

                    if (auth.Response.Result != ProviderResults.Authorized)
                    {
                        return (ProviderResults.Rejected, ProviderResultCodes.AuthorizationNotUsable, null);
                    }

                    if (await EffectExistsAsync(connection, tx, r.Reference!, ProviderKinds.Capture, ct))
                    {
                        return (ProviderResults.Rejected, ProviderResultCodes.AlreadyCaptured, null);
                    }

                    var expired = await ScalarAsync<int?>(connection, tx,
                        "SELECT CASE WHEN AuthorizationExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END FROM provider.Operation WHERE OperationKey = @k", ct, ("@k", r.Reference!));
                    if (expired == 1)
                    {
                        return (ProviderResults.Rejected, ProviderResultCodes.AuthorizationExpired, null);
                    }

                    return r.AmountMinor > auth.Response.AmountMinor || r.Currency != auth.Response.Currency
                        ? (ProviderResults.Rejected, ProviderResultCodes.AmountMismatch, null)
                        : (ProviderResults.Captured, null, null);
                }

            case ProviderKinds.Void:
                {
                    var auth = await ReadOperationAsync(connection, tx, r.Reference!, ct);
                    if (auth is null)
                    {
                        return (ProviderResults.NothingToVoid, ProviderResultCodes.ReferenceNeverCommitted, null);
                    }

                    if (auth.Response.Result != ProviderResults.Authorized || await EffectExistsAsync(connection, tx, r.Reference!, ProviderKinds.Void, ct))
                    {
                        return (ProviderResults.NothingToVoid, null, null);
                    }

                    return await EffectExistsAsync(connection, tx, r.Reference!, ProviderKinds.Capture, ct)
                        ? (ProviderResults.Rejected, ProviderResultCodes.AlreadyCaptured, null)
                        : (ProviderResults.Voided, null, null);
                }

            case ProviderKinds.Refund:
                {
                    var capture = await ReadOperationAsync(connection, tx, r.Reference!, ct);
                    if (capture is null)
                    {
                        return (ProviderResults.NothingToRefund, ProviderResultCodes.ReferenceNeverCommitted, null);
                    }

                    if (capture.Response.Result != ProviderResults.Captured || await EffectExistsAsync(connection, tx, r.Reference!, ProviderKinds.Refund, ct))
                    {
                        return (ProviderResults.NothingToRefund, null, null);
                    }

                    // Full refunds only (one refund per capture).
                    return r.AmountMinor != capture.Response.AmountMinor || r.Currency != capture.Response.Currency
                        ? (ProviderResults.Rejected, ProviderResultCodes.AmountMismatch, null)
                        : (ProviderResults.Refunded, null, null);
                }

            default:
                throw new ArgumentException($"unknown kind {r.Kind}", nameof(r));
        }
    }

    /// <summary>A status query: Committed, Prohibited, NotFound, or Unknown (fault). Every answer is logged.</summary>
    public async Task<ProviderOperationResponse> QueryAsync(string operationKey, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        ProviderOperationResponse answer;
        var unknown = await ScalarAsync<long?>(connection, tx,
            """
            SELECT TOP (1) Id FROM provider.Fault WITH (UPDLOCK, ROWLOCK)
            WHERE Mode = 'StatusUnknown' AND Match = @k AND (RemainingUses IS NULL OR RemainingUses > 0) ORDER BY Id
            """, ct, ("@k", operationKey));
        if (unknown is not null)
        {
            await ExecAsync(connection, tx, "UPDATE provider.Fault SET RemainingUses = RemainingUses - 1 WHERE Id = @id AND RemainingUses IS NOT NULL", ct, ("@id", unknown.Value));
            answer = new ProviderOperationResponse(operationKey, ProviderStatuses.Unknown);
        }
        else if (await ReadOperationAsync(connection, tx, operationKey, ct) is { } op)
        {
            answer = op.Response;
        }
        else
        {
            var by = await ScalarAsync<string>(connection, tx, "SELECT ByOperationKey FROM provider.Prohibition WHERE OperationKey = @k", ct, ("@k", operationKey));
            answer = by is null
                ? new ProviderOperationResponse(operationKey, ProviderStatuses.NotFound)
                : new ProviderOperationResponse(operationKey, ProviderStatuses.Prohibited, ProhibitedBy: by);
        }

        await ExecAsync(connection, tx, "INSERT provider.StatusQuery (OperationKey, Answer) VALUES (@k, @a)", ct, ("@k", operationKey), ("@a", answer.Status));
        await tx.CommitAsync(ct);
        return answer;
    }

    public static byte[] PayloadHash(ProviderOperationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', r.Kind, r.AmountMinor, r.Currency, r.Reference ?? "")));
    }

    /// <summary>Takes the faults that apply to this request (exact key first, then kind), using up counted ones.</summary>
    private static async Task<Dictionary<string, int?>> TakeFaultsAsync(SqlConnection connection, SqlTransaction tx, ProviderOperationRequest r, CancellationToken ct)
    {
        var found = new Dictionary<string, (long Id, int? Param)>(StringComparer.Ordinal);
        await using (var read = new SqlCommand(
                         """
                         SELECT Id, Mode, Param FROM provider.Fault WITH (UPDLOCK, ROWLOCK)
                         WHERE Match IN (@k, 'kind:' + @kind) AND Mode <> 'StatusUnknown' AND (RemainingUses IS NULL OR RemainingUses > 0)
                         ORDER BY CASE WHEN Match = @k THEN 0 ELSE 1 END, Id;
                         """, connection, tx))
        {
            read.Parameters.AddWithValue("@k", r.OperationKey);
            read.Parameters.AddWithValue("@kind", r.Kind);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                found.TryAdd(reader.GetString(1), (reader.GetInt64(0), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }

        foreach (var (_, (id, _)) in found)
        {
            await ExecAsync(connection, tx, "UPDATE provider.Fault SET RemainingUses = RemainingUses - 1 WHERE Id = @id AND RemainingUses IS NOT NULL", ct, ("@id", id));
        }

        return found.ToDictionary(f => f.Key, f => f.Value.Param, StringComparer.Ordinal);
    }

    private static async Task<bool> EffectExistsAsync(SqlConnection connection, SqlTransaction tx, string reference, string kind, CancellationToken ct) =>
        await ScalarAsync<int?>(connection, tx,
            "SELECT 1 FROM provider.Operation WHERE Reference = @r AND Kind = @kind AND Result IN ('Captured', 'Voided', 'Refunded')",
            ct, ("@r", reference), ("@kind", kind)) == 1;

    private sealed record StoredOperation(byte[] Hash, ProviderOperationResponse Response);

    private static async Task<StoredOperation?> ReadOperationAsync(SqlConnection connection, SqlTransaction tx, string key, CancellationToken ct)
    {
        await using var read = new SqlCommand(
            """
            SELECT PayloadHash, Kind, Result, ResultCode, AmountMinor, Currency, Reference, AuthorizationExpiresAt, CommittedAt
            FROM provider.Operation WHERE OperationKey = @k;
            """, connection, tx);
        read.Parameters.AddWithValue("@k", key);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var code = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new StoredOperation((byte[])reader[0], new ProviderOperationResponse(
            key, ProviderStatuses.Committed, reader.GetString(1), reader.GetString(2),
            code,
            reader.GetInt64(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
    }

    private static async Task LockKeyAsync(SqlConnection connection, SqlTransaction tx, string key, CancellationToken ct)
    {
        await using var command = new SqlCommand("sp_getapplock", connection, tx) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Resource", "provider-op:" + key);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Transaction");
        command.Parameters.AddWithValue("@LockTimeout", 30_000);
        var result = command.Parameters.Add("@Result", System.Data.SqlDbType.Int);
        result.Direction = System.Data.ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(ct);
        if ((int)result.Value < 0)
        {
            throw new TimeoutException($"could not lock operation key {key} (sp_getapplock {result.Value})");
        }
    }

    private static async Task ExecAsync(SqlConnection connection, SqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection connection, SqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value2 = await command.ExecuteScalarAsync(ct);
        return value2 is null or DBNull ? default : (T)value2;
    }
}
