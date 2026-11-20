using System.Security.Cryptography;
using System.Text;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Orders;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Reviews;

public enum DecisionOutcomeKind
{
    /// <summary>The decision was committed now (with its audit row and pending signal).</summary>
    Accepted,

    /// <summary>The same decision id and payload were accepted before: nothing written.</summary>
    Replayed,

    /// <summary>The decision id was used with another payload or another review: nothing written.</summary>
    DecisionIdReused,

    AlreadyDecided,
    Expired,
    Superseded,
    Cancelled,
    NotFound,
}

public sealed record DecisionResult(DecisionOutcomeKind Kind, string? Outcome);

/// <summary>Checkpoint names of the decision transaction (also drill failpoint names).</summary>
public static class DecisionCheckpoints
{
    public const string AfterReviewUpdate = "AfterReviewDecisionUpdate";
    public const string AfterDecisionInsert = "AfterReviewDecisionInsert";
    public const string AfterSignalInsert = "AfterPendingSignalInsert";

    /// <summary>After the decision committed, before any delivery of its signal (drills end the process here).</summary>
    public const string AfterCommit = "AfterReviewDecisionCommitBeforeSignalDispatch";
}

/// <summary>
/// The review decision protocol: SQL decides. One transaction locks the review, recognises a retry
/// of a decision id by its stored payload hash, requires the review to be Pending and SQL time to be
/// before its deadline (in the conditional update itself), records the immutable decision with the
/// operator, and writes the pending signal that wakes the workflow. Concurrent decisions serialize
/// on the review row; the unique decision-per-review index is the backstop. The order's status is
/// not changed here: the workflow applies the decision after it reads it.
/// </summary>
public sealed class ReviewDecisions(string connectionString, StepCheckpoint checkpoint)
{
    /// <summary>SHA-256 of a versioned normalized rendering of the decision payload.</summary>
    public static byte[] Hash(Guid reviewId, SubmitDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = $"v1\n{reviewId:N}\n{request.DecisionId:N}\n{request.OperatorId}\n{request.Outcome}\n{request.Comment ?? string.Empty}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    public static string Stored(string outcome) => outcome == DecisionOutcomes.Approve ? ReviewStates.Approved : ReviewStates.Rejected;

    public async Task<DecisionResult> DecideAsync(Guid reviewId, SubmitDecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = Hash(reviewId, request);
        var key = $"{request.DecisionId:N}";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        (string Status, Guid OrderId, string? InstanceId, bool LaterRound)? review;
        await using (var read = new SqlCommand(
                         """
                         SELECT r.Status, r.OrderId, s.InstanceId,
                                CASE WHEN EXISTS (SELECT 1 FROM orders.ReviewTask n WHERE n.OrderId = r.OrderId AND n.Purpose = r.Purpose AND n.Round > r.Round) THEN 1 ELSE 0 END
                         FROM orders.ReviewTask r WITH (UPDLOCK, ROWLOCK)
                         LEFT JOIN orders.WorkflowStartRequest s ON s.OrderId = r.OrderId
                         WHERE r.ReviewId = @r;
                         """, connection, tx))
        {
            read.Parameters.AddWithValue("@r", reviewId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            review = await reader.ReadAsync(ct)
                ? (reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3) == 1)
                : null;
        }

        if (review is null)
        {
            return new DecisionResult(DecisionOutcomeKind.NotFound, null);
        }

        // A known decision id: a retry of the same payload is harmless, anything else is a reuse.
        await using (var existing = new SqlCommand(
                         "SELECT ReviewId, PayloadHash, Outcome FROM orders.ReviewDecision WHERE DecisionId = @d;", connection, tx))
        {
            existing.Parameters.AddWithValue("@d", request.DecisionId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var same = reader.GetGuid(0) == reviewId && ((byte[])reader.GetValue(1)).AsSpan().SequenceEqual(hash);
                return new DecisionResult(same ? DecisionOutcomeKind.Replayed : DecisionOutcomeKind.DecisionIdReused, reader.GetString(2));
            }
        }

        switch (review.Value.Status)
        {
            case ReviewStates.Approved or ReviewStates.Rejected:
                return new DecisionResult(DecisionOutcomeKind.AlreadyDecided, review.Value.Status);
            case ReviewStates.Expired or ReviewStates.Escalated:
                return new DecisionResult(review.Value.LaterRound ? DecisionOutcomeKind.Superseded : DecisionOutcomeKind.Expired, review.Value.Status);
            case ReviewStates.Cancelled:
                return new DecisionResult(DecisionOutcomeKind.Cancelled, review.Value.Status);
        }

        var outcome = Stored(request.Outcome);
        await using (var decide = new SqlCommand(
                         """
                         UPDATE orders.ReviewTask SET Status = @s, DecidedAt = SYSUTCDATETIME(), DecisionId = @d
                         WHERE ReviewId = @r AND Status = 'Pending' AND SYSUTCDATETIME() < DeadlineAt;
                         """, connection, tx))
        {
            decide.Parameters.AddWithValue("@r", reviewId);
            decide.Parameters.AddWithValue("@s", outcome);
            decide.Parameters.AddWithValue("@d", request.DecisionId);
            if (await decide.ExecuteNonQueryAsync(ct) == 0)
            {
                // Pending, but SQL time reached the deadline: the decision comes too late.
                return new DecisionResult(DecisionOutcomeKind.Expired, ReviewStates.Pending);
            }
        }

        await checkpoint(DecisionCheckpoints.AfterReviewUpdate, key, ct);
        await using (var audit = new SqlCommand(
                         """
                         INSERT orders.ReviewDecision (DecisionId, ReviewId, OrderId, Outcome, OperatorId, Comment, PayloadHash)
                         VALUES (@d, @r, @o, @s, @op, @c, @h);
                         """, connection, tx))
        {
            audit.Parameters.AddWithValue("@d", request.DecisionId);
            audit.Parameters.AddWithValue("@r", reviewId);
            audit.Parameters.AddWithValue("@o", review.Value.OrderId);
            audit.Parameters.AddWithValue("@s", outcome);
            audit.Parameters.AddWithValue("@op", request.OperatorId);
            audit.Parameters.AddWithValue("@c", (object?)request.Comment ?? DBNull.Value);
            audit.Parameters.Add("@h", System.Data.SqlDbType.Binary, 32).Value = hash;
            await audit.ExecuteNonQueryAsync(ct);
        }

        await checkpoint(DecisionCheckpoints.AfterDecisionInsert, key, ct);
        await using (var signal = new SqlCommand(
                         """
                         INSERT orders.PendingSignal (OrderId, InstanceId, EventName, CausedBy, State, Attempts)
                         VALUES (@o, @i, @e, @d, 'Pending', 0);
                         """, connection, tx))
        {
            signal.Parameters.AddWithValue("@o", review.Value.OrderId);
            signal.Parameters.AddWithValue("@i", review.Value.InstanceId ?? FulfillmentWorkflow.InstanceId(review.Value.OrderId));
            signal.Parameters.AddWithValue("@e", ReviewSignals.EventName(reviewId));
            signal.Parameters.AddWithValue("@d", request.DecisionId);
            await signal.ExecuteNonQueryAsync(ct);
        }

        await checkpoint(DecisionCheckpoints.AfterSignalInsert, key, ct);
        await tx.CommitAsync(ct);
        await checkpoint(DecisionCheckpoints.AfterCommit, key, ct);
        return new DecisionResult(DecisionOutcomeKind.Accepted, outcome);
    }
}
