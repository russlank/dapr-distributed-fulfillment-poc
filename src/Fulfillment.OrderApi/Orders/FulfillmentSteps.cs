using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Fulfillment.Primitives;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Orders;

/// <summary>Business rules the workflow's validation applies (the API checks only syntax and bounds).</summary>
public static class OrderRules
{
    /// <summary>The first broken rule as a reason code, or null when the order is valid.</summary>
    public static string? FirstViolation(IReadOnlyList<ValidatedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return OrderReasonCodes.NoLines;
        }

        // One line per SKU: reservations and allocations work per SKU.
        return lines.Select(l => l.Sku).Distinct(StringComparer.Ordinal).Count() != lines.Count ? OrderReasonCodes.DuplicateSku : null;
    }
}

/// <summary>
/// The order-api steps of a fulfillment run, each one SQL transaction in OrderDb. Every step starts
/// by locking the order row, so the steps of one order (from any replica, any retry, any repeated
/// run) are serialized. Every effect is conditional or keyed: status changes require the expected
/// status, and effect rows are keyed by operation key, so an execution that finds its effect
/// committed returns the stored result instead of applying it again. A step for a terminal order
/// changes nothing and tells the run to end. The transaction that makes an order terminal also
/// closes its start request.
/// </summary>
public sealed class FulfillmentSteps(string connectionString, string replica, StepCheckpoint checkpoint)
{
    public async Task<OrderValidationResult> ValidateAsync(ValidateOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, customerId) = await LockOrThrowAsync(connection, tx, request.OrderId, ct);
        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new OrderValidationResult(StepOutcomes.AlreadyTerminal, status.ToString(), null, customerId, []);
        }

        var key = OperationKey.For(request.OrderId, "validate", "1").Value;
        if (status == OrderStatus.Submitted)
        {
            await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.Submitted, OrderStatus.Validating,
                OrderReasonCodes.ValidationStarted, checkpoint, key, ct);
        }
        else if (status != OrderStatus.Validating)
        {
            throw new InvalidOperationException($"validation of an order in status {status}");
        }

        var lines = await ReadLinesAsync(connection, tx, request.OrderId, ct);
        var violation = OrderRules.FirstViolation(lines);
        if (violation is not null)
        {
            await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.Validating, OrderStatus.Rejected,
                violation, checkpoint, key, ct);
            await OrderTransitions.CloseStartRequestAsync(connection, tx, request.OrderId, ct);
            await checkpoint(StepCheckpoints.AfterStartRequestClose, key, ct);
            await tx.CommitAsync(ct);
            return new OrderValidationResult(StepOutcomes.Rejected, nameof(OrderStatus.Rejected), violation, customerId, lines);
        }

        await tx.CommitAsync(ct);
        return new OrderValidationResult(StepOutcomes.Valid, nameof(OrderStatus.Validating), null, customerId, lines);
    }

    /// <summary>Stores each check's result once; a repeated execution keeps the first result.</summary>
    public async Task<StepResult> RecordChecksAsync(RecordChecksRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await LockOrThrowAsync(connection, tx, request.OrderId, ct);
        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyTerminal, null, status.ToString());
        }

        var price = await RecordCheckAsync(connection, tx, request.OrderId, FulfillmentChecks.Price, request.Price.Execution,
            request.Price.TotalMinor, request.Price.Currency, null, null, request.Price.PriceListVersion, ct);
        var risk = await RecordCheckAsync(connection, tx, request.OrderId, FulfillmentChecks.Risk, request.Risk.Execution,
            null, null, request.Risk.Score, request.Risk.ReviewRequired, request.Risk.RuleVersion, ct);
        await tx.CommitAsync(ct);
        return new StepResult(price && risk ? StepOutcomes.Applied : StepOutcomes.AlreadyApplied, null, status.ToString());
    }

    /// <summary>One simulated fulfillment step (not the completion), committed at most once per operation key.</summary>
    public async Task<StepResult> ApplyStubStepAsync(StubStepRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Step.StartsWith(StubSteps.Prefix, StringComparison.Ordinal) || request.Step == StubSteps.Complete)
        {
            throw new ArgumentException($"not a simulated fulfillment step: {request.Step}", nameof(request));
        }

        var key = OperationKey.For(request.OrderId, request.Step, request.Qualifier).Value;
        await checkpoint(StepCheckpoints.BeforeStubStep, key, ct);
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await LockOrThrowAsync(connection, tx, request.OrderId, ct);
        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyTerminal, key, status.ToString());
        }

        if (status != OrderStatus.Validating)
        {
            throw new InvalidOperationException($"simulated fulfillment step of an order in status {status}");
        }

        if (await CountReplayAsync(connection, tx, key, ct))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyApplied, key, status.ToString());
        }

        await InsertEffectAsync(connection, tx, request.OrderId, key, request.Step, request.Qualifier, ct);
        await checkpoint(StepCheckpoints.AfterEffectInsert, key, ct);
        await tx.CommitAsync(ct);
        await checkpoint(StepCheckpoints.AfterStubEffectCommit, key, ct);
        return new StepResult(StepOutcomes.Applied, key, status.ToString());
    }

    /// <summary>
    /// The terminal transaction: the stub completion effect, Validating → Completed with its history
    /// row, and the start request Closed, all or nothing. A repeated execution finds the effect and
    /// returns AlreadyApplied without any change.
    /// </summary>
    public async Task<StepResult> CompleteAsync(CompleteFulfillmentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = OperationKey.For(request.OrderId, StubSteps.Complete, "1").Value;
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var (status, _) = await LockOrThrowAsync(connection, tx, request.OrderId, ct);
        if (await CountReplayAsync(connection, tx, key, ct))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyApplied, key, status.ToString());
        }

        if (OrderStatuses.IsTerminal(status))
        {
            await tx.CommitAsync(ct);
            return new StepResult(StepOutcomes.AlreadyTerminal, key, status.ToString());
        }

        if (!await OrderTransitions.TryTransitionAsync(connection, tx, request.OrderId, OrderStatus.Validating, OrderStatus.Completed,
                OrderReasonCodes.StubFulfillmentCompleted, checkpoint, key, ct))
        {
            throw new InvalidOperationException($"completion of an order in status {status}");
        }

        await InsertEffectAsync(connection, tx, request.OrderId, key, StubSteps.Complete, "1", ct);
        await checkpoint(StepCheckpoints.AfterEffectInsert, key, ct);
        await OrderTransitions.CloseStartRequestAsync(connection, tx, request.OrderId, ct);
        await checkpoint(StepCheckpoints.AfterStartRequestClose, key, ct);
        await tx.CommitAsync(ct);
        await checkpoint(StepCheckpoints.AfterTerminalCommit, key, ct);
        return new StepResult(StepOutcomes.Applied, key, nameof(OrderStatus.Completed));
    }

    private static async Task<(OrderStatus Status, string CustomerId)> LockOrThrowAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct) =>
        await OrderTransitions.LockAsync(connection, tx, orderId, ct)
        ?? throw new InvalidOperationException("fulfillment step for an order that does not exist");

    private static async Task<List<ValidatedLine>> ReadLinesAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT LineNumber, Sku, Quantity FROM orders.OrderLine WHERE OrderId = @o ORDER BY LineNumber;", connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        var lines = new List<ValidatedLine>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(new ValidatedLine(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return lines;
    }

    /// <summary>True when the effect exists (its replay is counted); the key range stays locked to the end of the transaction.</summary>
    private static async Task<bool> CountReplayAsync(SqlConnection connection, SqlTransaction tx, string key, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "UPDATE orders.StubEffect WITH (HOLDLOCK) SET Replays = Replays + 1 WHERE OperationKey = @k;", connection, tx);
        command.Parameters.AddWithValue("@k", key);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task InsertEffectAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, string key, string step, string qualifier, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            INSERT orders.StubEffect (OperationKey, OrderId, Step, Qualifier, AppliedBy, Replays)
            VALUES (@k, @o, @s, @q, @r, 0);
            """, connection, tx);
        command.Parameters.AddWithValue("@k", key);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@s", step);
        command.Parameters.AddWithValue("@q", qualifier);
        command.Parameters.AddWithValue("@r", replica);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Inserts the check's result unless one exists (then counts the replay); true when inserted.</summary>
    private static async Task<bool> RecordCheckAsync(SqlConnection connection, SqlTransaction tx, Guid orderId, string check, CheckExecution execution,
        long? totalMinor, string? currency, int? score, bool? reviewRequired, string ruleVersion, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            UPDATE orders.OrderCheck WITH (HOLDLOCK) SET Replays = Replays + 1 WHERE OrderId = @o AND CheckName = @c;
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT orders.OrderCheck (OrderId, CheckName, OperationKey, Replica, StartedAt, CompletedAt,
                                          TotalMinor, Currency, RiskScore, ReviewRequired, RuleVersion, Replays)
                VALUES (@o, @c, @k, @replica, @started, @completed, @total, @currency, @score, @review, @version, 0);
                SELECT 1;
            END
            ELSE SELECT 0;
            """, connection, tx);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@c", check);
        command.Parameters.AddWithValue("@k", OperationKey.For(orderId, FulfillmentChecks.Step, check).Value);
        command.Parameters.AddWithValue("@replica", execution.Replica);
        command.Parameters.Add("@started", System.Data.SqlDbType.DateTime2).Value = execution.StartedAtUtc;
        command.Parameters.Add("@completed", System.Data.SqlDbType.DateTime2).Value = execution.CompletedAtUtc;
        command.Parameters.AddWithValue("@total", (object?)totalMinor ?? DBNull.Value);
        command.Parameters.AddWithValue("@currency", (object?)currency ?? DBNull.Value);
        command.Parameters.AddWithValue("@score", (object?)score ?? DBNull.Value);
        command.Parameters.AddWithValue("@review", (object?)reviewRequired ?? DBNull.Value);
        command.Parameters.AddWithValue("@version", ruleVersion);
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
