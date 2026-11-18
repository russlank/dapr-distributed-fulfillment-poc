using Fulfillment.OrderApi.Domain;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Dispatch;

/// <summary>A start request claimed by one dispatcher; every later change must present the token.</summary>
public sealed record ClaimedStartRequest(Guid OrderId, string InstanceId, int Attempt, Guid LeaseToken);

public sealed record ClaimBatch(IReadOnlyList<ClaimedStartRequest> Claimed, IReadOnlyList<Guid> ClosedTerminal);

/// <summary>Result codes of start attempts (closed code domain, matches CK_WorkflowStartAttempt_Result).</summary>
public static class StartAttemptResults
{
    public const string Started = "Started";
    public const string AlreadyExists = "AlreadyExists";
    public const string Ambiguous = "Ambiguous";
    public const string Rejected = "Rejected";
    public const string LeaseLost = "LeaseLost";
}

/// <summary>Error codes stored on a start request (closed code domain).</summary>
public static class StartErrorCodes
{
    public const string OrderTerminal = "OrderTerminal";
    public const string StartAmbiguous = "StartAmbiguous";
    public const string StartRejected = "StartRejected";
}

/// <summary>
/// SQL coordination of the start dispatchers. Claims take due Pending rows with UPDLOCK and READPAST,
/// so concurrent dispatchers (any replica) never claim the same row at once and never wait for each
/// other; a row whose lease expired is claimable again. Each claim issues a new lease token, and
/// every later change (started, released) is conditional on it: a dispatcher whose lease was taken
/// over changes nothing. The terminal guard runs in the claiming transaction.
/// </summary>
public sealed class WorkflowStartStore(string connectionString)
{
    private static readonly string TerminalList = string.Join(", ",
        Enum.GetValues<OrderStatus>().Where(OrderStatuses.IsTerminal).Select(s => $"'{s}'"));

    public async Task<ClaimBatch> ClaimDueAsync(string dispatcherId, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @due TABLE (OrderId uniqueidentifier PRIMARY KEY, Terminal bit NOT NULL);
            INSERT @due (OrderId, Terminal)
            SELECT TOP (@batch) r.OrderId, CASE WHEN o.Status IN ({TerminalList}) THEN 1 ELSE 0 END
            FROM orders.WorkflowStartRequest r WITH (UPDLOCK, READPAST, ROWLOCK)
            JOIN orders.[Order] o WITH (READCOMMITTEDLOCK) ON o.OrderId = r.OrderId
            WHERE r.State = 'Pending' AND r.NextAttemptAt <= SYSUTCDATETIME()
              AND (r.LeaseExpiresAt IS NULL OR r.LeaseExpiresAt <= SYSUTCDATETIME())
            ORDER BY r.NextAttemptAt;

            -- Terminal guard: a terminal order is never started; its start request is closed.
            UPDATE r SET State = 'Closed', ClosedAt = SYSUTCDATETIME(), LastErrorCode = '{StartErrorCodes.OrderTerminal}',
                         LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
            OUTPUT inserted.OrderId
            FROM orders.WorkflowStartRequest r JOIN @due d ON d.OrderId = r.OrderId WHERE d.Terminal = 1;

            UPDATE r SET LeaseOwner = @owner, LeaseToken = NEWID(), Attempts = Attempts + 1,
                         LeaseExpiresAt = DATEADD(millisecond, @leaseMs, SYSUTCDATETIME())
            OUTPUT inserted.OrderId, inserted.InstanceId, inserted.Attempts, inserted.LeaseToken
            FROM orders.WorkflowStartRequest r JOIN @due d ON d.OrderId = r.OrderId WHERE d.Terminal = 0;
            COMMIT;
            """, connection);
        command.Parameters.AddWithValue("@batch", batchSize);
        command.Parameters.AddWithValue("@owner", dispatcherId);
        command.Parameters.AddWithValue("@leaseMs", (int)lease.TotalMilliseconds);

        var closed = new List<Guid>();
        var claimed = new List<ClaimedStartRequest>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            closed.Add(reader.GetGuid(0));
        }

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            claimed.Add(new ClaimedStartRequest(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetGuid(3)));
        }

        return new ClaimBatch(claimed, closed);
    }

    /// <summary>Appends the attempt record before the workflow host is called; returns its id.</summary>
    public async Task<long> RecordAttemptAsync(ClaimedStartRequest claim, string dispatcherId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            INSERT orders.WorkflowStartAttempt (OrderId, LeaseToken, DispatcherId, InstanceId, Attempt)
            OUTPUT inserted.Id VALUES (@o, @t, @d, @i, @a);
            """, connection);
        command.Parameters.AddWithValue("@o", claim.OrderId);
        command.Parameters.AddWithValue("@t", claim.LeaseToken);
        command.Parameters.AddWithValue("@d", dispatcherId);
        command.Parameters.AddWithValue("@i", claim.InstanceId);
        command.Parameters.AddWithValue("@a", claim.Attempt);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task CompleteAttemptAsync(long attemptId, string result, string? reportedStatus, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            "UPDATE orders.WorkflowStartAttempt SET CompletedAt = SYSUTCDATETIME(), Result = @r, ReportedStatus = @s WHERE Id = @id;", connection);
        command.Parameters.AddWithValue("@id", attemptId);
        command.Parameters.AddWithValue("@r", result);
        command.Parameters.AddWithValue("@s", (object?)reportedStatus ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Marks the request Started; false when the lease was lost (the row is unchanged).</summary>
    public async Task<bool> MarkStartedAsync(ClaimedStartRequest claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return await ExecuteFencedAsync(
            """
            UPDATE orders.WorkflowStartRequest
            SET State = 'Started', StartedAt = SYSUTCDATETIME(), LastErrorCode = NULL,
                LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
            WHERE OrderId = @o AND LeaseToken = @t AND State = 'Pending';
            """, claim, null, TimeSpan.Zero, ct);
    }

    /// <summary>Releases the lease for a later attempt; false when the lease was lost (the row is unchanged).</summary>
    public async Task<bool> ReleaseForRetryAsync(ClaimedStartRequest claim, string errorCode, TimeSpan backoff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return await ExecuteFencedAsync(
            """
            UPDATE orders.WorkflowStartRequest
            SET LastErrorCode = @code, NextAttemptAt = DATEADD(millisecond, @backoffMs, SYSUTCDATETIME()),
                LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
            WHERE OrderId = @o AND LeaseToken = @t AND State = 'Pending';
            """, claim, errorCode, backoff, ct);
    }

    private async Task<bool> ExecuteFencedAsync(string sql, ClaimedStartRequest claim, string? code, TimeSpan backoff, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@o", claim.OrderId);
        command.Parameters.AddWithValue("@t", claim.LeaseToken);
        if (code is not null)
        {
            command.Parameters.AddWithValue("@code", code);
            command.Parameters.AddWithValue("@backoffMs", (int)backoff.TotalMilliseconds);
        }

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
