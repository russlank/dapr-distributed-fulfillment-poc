using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Fulfillment.OrderApi.Orders;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Reviews;

/// <summary>Review policy (time profile: drill or demo; see <see cref="ForEnvironment"/>).</summary>
public sealed class ReviewOptions
{
    public const string Section = "Review";

    /// <summary>Time a round stays open for a decision (measured by SQL time).</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The workflow's timer fires this long after the deadline (clock and delivery slack).</summary>
    public TimeSpan TimerGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Rounds including the first; after the last one expires the order is Rejected.</summary>
    public int Rounds { get; set; } = 2;

    /// <summary>Simulated operator ids allowed to decide (comma-separated).</summary>
    public string Operators { get; set; } = "operator-1,operator-2";

    public bool IsOperator(string operatorId) =>
        Operators.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(operatorId, StringComparer.Ordinal);

    /// <summary>Defaults of the time profile: the drill environment uses short deadlines (20 s, grace 2 s).</summary>
    public static void ForEnvironment(ReviewOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(environment, "drill", StringComparison.Ordinal))
        {
            options.Deadline = TimeSpan.FromSeconds(20);
            options.TimerGrace = TimeSpan.FromSeconds(2);
        }
    }
}

/// <summary>Checkpoint names of the review steps (also drill failpoint names).</summary>
public static class ReviewCheckpoints
{
    /// <summary>After round 1 committed, before the activity returns: drills hold the run here, before its first wait (key: {orderId:N}:review:1).</summary>
    public const string AfterReviewTaskCreateCommit = "AfterReviewTaskCreateCommit";
}

/// <summary>
/// The review steps of a fulfillment run (activities on order-api). Each locks the order row first,
/// like every fulfillment step, and is safe to repeat. A review's decision comes only from the
/// decision endpoint (<see cref="ReviewDecisions"/>); here a review only leaves Pending by time,
/// through <see cref="ExpireOrEscalateAsync"/>, and only when SQL time has reached its deadline.
/// </summary>
public sealed class ReviewSteps(string connectionString, ReviewOptions options, StepCheckpoint checkpoint)
{
    /// <summary>
    /// Opens round 1 for an order whose stored risk result requires a review (Validating →
    /// AwaitingApproval). Repeated or later runs get the order's latest round back unchanged.
    /// </summary>
    public async Task<ReviewRound> CreateAsync(CreateReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        var latest = await LatestRoundAsync(connection, tx, request.OrderId, ct);
        if (latest is not null)
        {
            await tx.CommitAsync(ct);
            return Round(OrderStatuses.IsTerminal(status) ? StepOutcomes.AlreadyTerminal : StepOutcomes.AlreadyApplied, latest.Value, status);
        }

        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new ReviewRound(StepOutcomes.AlreadyTerminal, Guid.Empty, 0, default, options.TimerGrace, status.ToString());
        }

        if (status != OrderStatus.Validating || await FulfillmentSteps.StoredRiskAsync(connection, tx, request.OrderId, ct) is not { ReviewRequired: true })
        {
            throw new InvalidOperationException($"a review can only be opened for a validated order whose stored risk result requires one (status {status})");
        }

        var key = $"{request.OrderId:N}:review:1";
        await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.Validating, OrderStatus.AwaitingApproval,
            OrderReasonCodes.ReviewRequired, checkpoint, key, ct);
        var created = await InsertRoundAsync(connection, tx, request.OrderId, 1, ct);
        await tx.CommitAsync(ct);
        await checkpoint(ReviewCheckpoints.AfterReviewTaskCreateCommit, key, ct);
        return Round(StepOutcomes.Applied, created, OrderStatus.AwaitingApproval);
    }

    /// <summary>
    /// Reads the review after a wake-up (recorded with the reason) and applies a rejection to the
    /// order (AwaitingApproval → Rejected, start request closed). An approval is applied by
    /// BeginFulfillment; a Pending review reports whether SQL time reached the deadline.
    /// </summary>
    public async Task<ReviewResolution> ResolveAsync(ResolveReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        var review = await ReadAsync(connection, tx, request.ReviewId, request.OrderId, ct);
        await using (var wake = new SqlCommand(
                         "INSERT orders.ReviewWake (ReviewId, Wake, SeenStatus, DeadlinePassed) VALUES (@r, @w, @s, @p);", connection, tx))
        {
            wake.Parameters.AddWithValue("@r", request.ReviewId);
            wake.Parameters.AddWithValue("@w", request.Wake);
            wake.Parameters.AddWithValue("@s", review.Status);
            wake.Parameters.AddWithValue("@p", review.DeadlinePassed);
            await wake.ExecuteNonQueryAsync(ct);
        }

        if (review.Status == ReviewStates.Pending && status == OrderStatus.AwaitingApproval
            && await CancellationSteps.CancelIfRequestedAsync(connection, tx, request.OrderId, status, checkpoint, ct))
        {
            await tx.CommitAsync(ct);
            return new ReviewResolution(ReviewStates.Cancelled, review.DeadlinePassed, nameof(OrderStatus.Cancelled), true);
        }

        if (review.Status == ReviewStates.Rejected && status == OrderStatus.AwaitingApproval)
        {
            var key = $"{request.OrderId:N}:review-rejected:{review.Round}";
            await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.AwaitingApproval, OrderStatus.Rejected,
                OrderReasonCodes.ReviewRejected, checkpoint, key, ct);
            await OrderTransitions.CloseStartRequestAsync(connection, tx, request.OrderId, ct);
            status = OrderStatus.Rejected;
        }

        await tx.CommitAsync(ct);
        return new ReviewResolution(review.Status, review.DeadlinePassed, status.ToString(), OrderStatuses.IsTerminal(status));
    }

    /// <summary>
    /// Expires the round if SQL time reached its deadline and it is still Pending; then opens the next
    /// round (fresh deadline) or, after the last round, rejects the order (ReviewExpired) and closes
    /// its start request. Repeats return the same result; a decided review is never expired.
    /// </summary>
    public async Task<ExpiryResult> ExpireOrEscalateAsync(ExpireReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await FulfillmentSteps.LockOrThrowAsync(connection, tx, request.OrderId, ct);
        var review = await ReadAsync(connection, tx, request.ReviewId, request.OrderId, ct);
        if (OrderStatuses.IsTerminal(status) && review.Status != ReviewStates.Expired)
        {
            await tx.CommitAsync(ct);
            return new ExpiryResult(ExpiryOutcomes.AlreadyTerminal, null, status.ToString());
        }

        if (review.Status is ReviewStates.Approved or ReviewStates.Rejected)
        {
            await tx.CommitAsync(ct);
            return new ExpiryResult(ExpiryOutcomes.AlreadyDecided, null, status.ToString());
        }

        if (review.Status == ReviewStates.Pending)
        {
            await using var expire = new SqlCommand(
                """
                UPDATE orders.ReviewTask SET Status = 'Expired', ExpiredAt = SYSUTCDATETIME()
                WHERE ReviewId = @r AND Status = 'Pending' AND SYSUTCDATETIME() >= DeadlineAt;
                """, connection, tx);
            expire.Parameters.AddWithValue("@r", request.ReviewId);
            if (await expire.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.CommitAsync(ct);
                return new ExpiryResult(ExpiryOutcomes.NotDue, null, status.ToString());
            }
        }

        // The round is Expired (now or by an earlier execution).
        if (review.Round < options.Rounds)
        {
            var next = await RoundAsync(connection, tx, request.OrderId, review.Round + 1, ct)
                       ?? await InsertRoundAsync(connection, tx, request.OrderId, review.Round + 1, ct);
            await tx.CommitAsync(ct);
            return new ExpiryResult(ExpiryOutcomes.NextRound, Round(StepOutcomes.Applied, next, status), status.ToString());
        }

        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new ExpiryResult(ExpiryOutcomes.FinalExpiry, null, status.ToString());
        }

        if (status != OrderStatus.AwaitingApproval)
        {
            throw new InvalidOperationException($"final expiry of a review of an order in status {status}");
        }

        {
            var key = $"{request.OrderId:N}:review-expired:{review.Round}";
            await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.AwaitingApproval, OrderStatus.Rejected,
                OrderReasonCodes.ReviewExpired, checkpoint, key, ct);
            await OrderTransitions.CloseStartRequestAsync(connection, tx, request.OrderId, ct);
            status = OrderStatus.Rejected;
        }

        await tx.CommitAsync(ct);
        return new ExpiryResult(ExpiryOutcomes.FinalExpiry, null, status.ToString());
    }

    private ReviewRound Round(string outcome, StoredRound round, OrderStatus status) =>
        new(outcome, round.ReviewId, round.Round, DateTime.SpecifyKind(round.DeadlineAt, DateTimeKind.Utc), options.TimerGrace, status.ToString());

    private async Task<StoredRound> InsertRoundAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, int round, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            INSERT orders.ReviewTask (OrderId, Purpose, Round, Status, DeadlineAt)
            OUTPUT inserted.ReviewId, inserted.Round, inserted.DeadlineAt
            VALUES (@o, 'Approval', @round, 'Pending', DATEADD(millisecond, @ms, SYSUTCDATETIME()));
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@round", round);
        command.Parameters.AddWithValue("@ms", (int)options.Deadline.TotalMilliseconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new StoredRound(reader.GetGuid(0), reader.GetInt32(1), reader.GetDateTime(2));
    }

    private static async Task<StoredRound?> LatestRoundAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct) =>
        await QueryRoundAsync(connection, tx,
            "SELECT TOP (1) ReviewId, Round, DeadlineAt FROM orders.ReviewTask WHERE OrderId = @o AND Purpose = 'Approval' ORDER BY Round DESC;",
            orderId, null, ct);

    private static async Task<StoredRound?> RoundAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, int round, CancellationToken ct) =>
        await QueryRoundAsync(connection, tx,
            "SELECT ReviewId, Round, DeadlineAt FROM orders.ReviewTask WHERE OrderId = @o AND Purpose = 'Approval' AND Round = @round;",
            orderId, round, ct);

    private static async Task<StoredRound?> QueryRoundAsync(SqlConnection connection, SqlTransaction tx, string sql, Guid orderId, int? round, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        if (round is not null)
        {
            command.Parameters.AddWithValue("@round", round.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new StoredRound(reader.GetGuid(0), reader.GetInt32(1), reader.GetDateTime(2)) : null;
    }

    private static async Task<(string Status, int Round, bool DeadlinePassed)> ReadAsync(SqlConnection connection, SqlTransaction tx, Guid reviewId, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            SELECT Status, Round, CASE WHEN SYSUTCDATETIME() >= DeadlineAt THEN 1 ELSE 0 END
            FROM orders.ReviewTask WITH (UPDLOCK, ROWLOCK) WHERE ReviewId = @r AND OrderId = @o;
            """, connection, tx);
        command.Parameters.AddWithValue("@r", reviewId);
        command.Parameters.AddWithValue("@o", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("review of another order, or no such review");
        }

        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2) == 1);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private readonly record struct StoredRound(Guid ReviewId, int Round, DateTime DeadlineAt);
}
