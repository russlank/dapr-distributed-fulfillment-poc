using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Orders;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>Drives the order-api steps of a fulfillment run directly (the workflow's activities without Dapr).</summary>
public static class Fulfilling
{
    private static readonly CheckExecution Ran = new("compute-test", new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 18, 12, 0, 0, 300, DateTimeKind.Utc));

    public static FulfillmentSteps Steps(string connectionString, StepCheckpoint? checkpoint = null, string replica = "it-replica") =>
        new(connectionString, replica, checkpoint ?? StepCheckpoints.None);

    public static RecordChecksRequest Checks(Guid orderId, long total = 1000, int score = 10, bool review = false, string version = "stub-v2") =>
        new(orderId, new PriceQuote(total, "EUR", "stub-v1", Ran), new RiskAssessment(score, review, version, Ran));

    /// <summary>Validation, checks without review, and BeginFulfillment: the order is Reserving.</summary>
    public static async Task<OrderValidationResult> EnterFulfillmentAsync(FulfillmentSteps steps, Guid orderId, CancellationToken ct = default)
    {
        var validation = await steps.ValidateAsync(new ValidateOrderRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), ct);
        await steps.RecordChecksAsync(Checks(orderId), ct);
        await steps.BeginFulfillmentAsync(new BeginFulfillmentRequest(orderId), ct);
        return validation;
    }

    /// <summary>Everything before completion, without review: the order is Reserving with every stub line step applied.</summary>
    public static async Task<OrderValidationResult> PrepareAsync(FulfillmentSteps steps, Guid orderId, CancellationToken ct = default)
    {
        var validation = await EnterFulfillmentAsync(steps, orderId, ct);
        foreach (var line in validation.Lines)
        {
            await steps.ApplyStubStepAsync(new StubStepRequest(orderId, StubSteps.Fulfil, StubSteps.LineQualifier(line.LineNumber)), ct);
        }

        return validation;
    }

    /// <summary>The whole run's order-api steps, as the workflow calls them.</summary>
    public static async Task<StepResult> RunAsync(FulfillmentSteps steps, Guid orderId, CancellationToken ct = default)
    {
        await PrepareAsync(steps, orderId, ct);
        return await steps.CompleteAsync(new CompleteFulfillmentRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), ct);
    }

    public static Task<StepResult> CompleteAsync(FulfillmentSteps steps, Guid orderId, CancellationToken ct = default) =>
        steps.CompleteAsync(new CompleteFulfillmentRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), ct);

    /// <summary>Everything the steps write for one order, as comparable text (the fingerprint of S49, in tests).</summary>
    public static async Task<OrderSnapshot> SnapshotAsync(string connectionString, Guid orderId)
    {
        var status = await Db.ScalarAsync<string>(connectionString, "SELECT Status FROM orders.[Order] WHERE OrderId = @o", ("@o", orderId));
        var history = (await Db.RowsAsync(connectionString,
                "SELECT Sequence, FromStatus, ToStatus, ReasonCode FROM orders.OrderStatusHistory WHERE OrderId = @o ORDER BY Sequence", ("@o", orderId)))
            .Select(r => $"{r["Sequence"]}:{r["FromStatus"]}>{r["ToStatus"]}:{r["ReasonCode"]}").ToList();
        var effects = (await Db.RowsAsync(connectionString,
                "SELECT OperationKey, Replays FROM orders.StubEffect WHERE OrderId = @o ORDER BY OperationKey", ("@o", orderId)))
            .ToDictionary(r => (string)r["OperationKey"]!, r => (int)r["Replays"]!);
        var checks = (await Db.RowsAsync(connectionString,
                "SELECT CheckName, TotalMinor, RiskScore, Replays FROM orders.OrderCheck WHERE OrderId = @o ORDER BY CheckName", ("@o", orderId)))
            .Select(r => $"{r["CheckName"]}:{r["TotalMinor"]}:{r["RiskScore"]}").ToList();
        var request = (await Db.RowsAsync(connectionString,
            "SELECT State, LastErrorCode, LeaseToken, StartedAt, ClosedAt FROM orders.WorkflowStartRequest WHERE OrderId = @o", ("@o", orderId))).Single();
        return new OrderSnapshot(status, history, effects, checks, (string)request["State"]!, (string?)request["LastErrorCode"], request["LeaseToken"] is not null,
            request["StartedAt"] is not null, request["ClosedAt"] is not null);
    }
}

public sealed record OrderSnapshot(
    string Status,
    IReadOnlyList<string> History,
    IReadOnlyDictionary<string, int> Effects,
    IReadOnlyList<string> Checks,
    string RequestState,
    string? RequestError,
    bool Leased,
    bool StartedAtSet,
    bool ClosedAtSet);
