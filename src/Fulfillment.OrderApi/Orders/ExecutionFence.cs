using System.Diagnostics.Metrics;
using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Orders;

/// <summary>
/// The execution fence in OrderDb. Every fulfillment run claims execution first: a run for a
/// terminal order is rejected (counted, audited) and must end without effects; any other claim is
/// counted and proceeds. More than one claim for a non-terminal order is normal after a replay,
/// restart, or a second start; the effects are keyed and therefore not repeated. The order row is
/// read with an update lock, so the claim sees the latest committed status and serializes with
/// status transitions of the same order.
/// </summary>
public sealed class ExecutionFence(string connectionString)
{
    private static readonly Meter Meter = new("Fulfillment.OrderApi.Fence");
    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>("fence_rejections_total",
        description: "execution claims rejected because the order was already terminal");

    private static readonly string TerminalList = string.Join(", ",
        Enum.GetValues<OrderStatus>().Where(OrderStatuses.IsTerminal).Select(s => $"'{s}'"));

    public async Task<ExecutionClaimResult> ClaimAsync(ExecutionClaimRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @status varchar(32) = (SELECT Status FROM orders.[Order] WITH (UPDLOCK, ROWLOCK) WHERE OrderId = @o);
            DECLARE @terminal bit = CASE WHEN @status IN ({TerminalList}) THEN 1 ELSE 0 END;
            DECLARE @claims TABLE (ExecutionClaims int);
            UPDATE orders.WorkflowStartRequest
            SET ExecutionClaims = ExecutionClaims + CASE WHEN @terminal = 1 THEN 0 ELSE 1 END,
                FenceRejections = FenceRejections + CASE WHEN @terminal = 1 THEN 1 ELSE 0 END
            OUTPUT inserted.ExecutionClaims INTO @claims
            WHERE OrderId = @o AND @status IS NOT NULL;
            IF @@ROWCOUNT = 1
                INSERT orders.ExecutionClaim (OrderId, InstanceId, Outcome, OrderStatus)
                VALUES (@o, @i, CASE WHEN @terminal = 1 THEN '{ExecutionClaimOutcomes.AlreadyTerminal}' ELSE '{ExecutionClaimOutcomes.Proceed}' END, @status);
            COMMIT;
            SELECT @status, @terminal, (SELECT ExecutionClaims FROM @claims);
            """, connection);
        command.Parameters.AddWithValue("@o", request.OrderId);
        command.Parameters.AddWithValue("@i", request.InstanceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.IsDBNull(0) || reader.IsDBNull(2))
        {
            // A fulfillment instance without an order or start request is a defect, not a fence decision.
            throw new InvalidOperationException("execution claim for an order without order row or start request");
        }

        var status = reader.GetString(0);
        var terminal = reader.GetBoolean(1);
        if (terminal)
        {
            Rejections.Add(1);
        }

        return new ExecutionClaimResult(terminal ? ExecutionClaimOutcomes.AlreadyTerminal : ExecutionClaimOutcomes.Proceed, status, reader.GetInt32(2));
    }

    /// <summary>Records the workflow version executing the order; idempotent, never lowers a recorded version. Returns the stored version.</summary>
    public async Task<int> RecordVersionAsync(ExecutionVersionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            UPDATE orders.WorkflowStartRequest SET ExecutionVersion = @v WHERE OrderId = @o AND (ExecutionVersion IS NULL OR ExecutionVersion < @v);
            SELECT ISNULL(ExecutionVersion, 0) FROM orders.WorkflowStartRequest WHERE OrderId = @o;
            """, connection);
        command.Parameters.AddWithValue("@o", request.OrderId);
        command.Parameters.AddWithValue("@v", request.Version);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }
}

/// <summary>Records the workflow version executing an order (patch execution-version-2).</summary>
public sealed class RecordFulfillmentExecutionVersionActivity(ExecutionFence fence) : WorkflowActivity<ExecutionVersionRequest, int>
{
    public override Task<int> RunAsync(WorkflowActivityContext context, ExecutionVersionRequest input) => fence.RecordVersionAsync(input);
}

/// <summary>First activity of every fulfillment run (hosted here because it writes OrderDb).</summary>
public sealed class ClaimFulfillmentExecutionActivity(ExecutionFence fence) : WorkflowActivity<ExecutionClaimRequest, ExecutionClaimResult>
{
    public override Task<ExecutionClaimResult> RunAsync(WorkflowActivityContext context, ExecutionClaimRequest input) => fence.ClaimAsync(input);
}
