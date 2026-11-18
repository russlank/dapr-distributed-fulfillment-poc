using Fulfillment.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>The migrations and the invariants the database itself enforces.</summary>
public sealed class SchemaTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Migrate_RunTwice_IsIdempotent()
    {
        var db = await sql.CreateDatabaseAsync("migrate");

        await SqlServerFixture.MigrateAsync(db);
        await SqlServerFixture.MigrateAsync(db);

        var applied = await Db.RowsAsync(db, "SELECT MigrationId FROM orders.__EFMigrationsHistory");
        Assert.Contains(applied, r => ((string)r["MigrationId"]!).EndsWith("_InitialOrderSchema", StringComparison.Ordinal));
        Assert.Equal(applied.Count, applied.Select(r => r["MigrationId"]).Distinct().Count());
    }

    [Fact]
    public async Task OrderApi_StartOnUnmigratedDatabase_RefusesToStart()
    {
        var db = await sql.CreateDatabaseAsync("unmigrated");
        await using var host = new OrderApiHost(db);

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("pending migration", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await Db.ScalarAsync<int>(db, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'orders'"));
    }

    public static TheoryData<string, string> Violations() => new()
    {
        // A second start request for one order (one start request per order).
        { "INSERT orders.WorkflowStartRequest (OrderId, InstanceId, State, Attempts, ExecutionClaims, FenceRejections) VALUES (@o, 'fulfill-other', 'Pending', 0, 0, 0)", "PK_WorkflowStartRequest" },
        // A second order for one idempotency key.
        { "INSERT orders.IdempotencyKey ([Key], RequestHash, OrderId) VALUES (@k, 0x" + new string('0', 64) + ", @o)", "PK_IdempotencyKey" },
        { "INSERT orders.OrderLine (OrderId, LineNumber, Sku, Quantity) VALUES (@o, 2, 'SKU-1', 101)", "CK_OrderLine_Quantity" },
        { "INSERT orders.OrderLine (OrderId, LineNumber, Sku, Quantity) VALUES (@o, 21, 'SKU-1', 1)", "CK_OrderLine_LineNumber" },
        { "UPDATE orders.[Order] SET Status = 'Shipped' WHERE OrderId = @o", "CK_Order_Status" },
        { "INSERT orders.OrderStatusHistory (OrderId, Sequence, ToStatus, ReasonCode) VALUES (@o, 1, 'Validating', 'Test')", "UX_OrderStatusHistory_Order_Sequence" },
        { "UPDATE orders.WorkflowStartRequest SET State = 'Started' WHERE OrderId = @o", "CK_WorkflowStartRequest_Lifecycle" },
        { "UPDATE orders.WorkflowStartRequest SET State = 'Closed' WHERE OrderId = @o", "CK_WorkflowStartRequest_Lifecycle" },
        { "UPDATE orders.WorkflowStartRequest SET LeaseOwner = 'order-api' WHERE OrderId = @o", "CK_WorkflowStartRequest_Lease" },
        { "UPDATE orders.WorkflowStartRequest SET ExecutionClaims = -1 WHERE OrderId = @o", "CK_WorkflowStartRequest_Counters" },
        { "UPDATE orders.WorkflowStartRequest SET InstanceId = 'fulfill-x' WHERE OrderId = @o; INSERT orders.[Order] (OrderId, CustomerId, Status) VALUES (@o2, 'c', 'Submitted'); INSERT orders.WorkflowStartRequest (OrderId, InstanceId, State, Attempts, ExecutionClaims, FenceRejections) VALUES (@o2, 'fulfill-x', 'Pending', 0, 0, 0)", "UX_WorkflowStartRequest_Instance" },
        { "INSERT orders.WorkflowStartRequest (OrderId, InstanceId, State, Attempts, ExecutionClaims, FenceRejections) VALUES (@o2, 'fulfill-orphan', 'Pending', 0, 0, 0)", "FK_WorkflowStartRequest_Order_OrderId" },
        // One simulated effect per operation key; simulated steps are recognisable by name.
        { "INSERT orders.StubEffect (OperationKey, OrderId, Step, Qualifier, AppliedBy, Replays) VALUES ('k1', @o, 'stub-fulfil', 'line-1', 'r', 0), ('k1', @o, 'stub-fulfil', 'line-1', 'r', 0)", "PK_StubEffect" },
        { "INSERT orders.StubEffect (OperationKey, OrderId, Step, Qualifier, AppliedBy, Replays) VALUES ('k2', @o, 'reserve', 'line-1', 'r', 0)", "CK_StubEffect_Step" },
        // One stored result per check.
        { "INSERT orders.OrderCheck (OrderId, CheckName, OperationKey, Replica, StartedAt, CompletedAt, RuleVersion, Replays) VALUES (@o, 'price', 'c1', 'r', SYSUTCDATETIME(), SYSUTCDATETIME(), 'v', 0), (@o, 'price', 'c2', 'r', SYSUTCDATETIME(), SYSUTCDATETIME(), 'v', 0)", "PK_OrderCheck" },
        { "INSERT orders.WorkflowStartAttempt (OrderId, LeaseToken, DispatcherId, InstanceId, Attempt, Result) VALUES (@o, NEWID(), 'd', 'i', 1, 'Lost')", "CK_WorkflowStartAttempt_Result" },
        // One review per (order, purpose, round); a consistent review lifecycle; known signal states.
        { "INSERT orders.ReviewTask (OrderId, Purpose, Round, Status, DeadlineAt) VALUES (@o, 'Approval', 1, 'Pending', SYSUTCDATETIME()), (@o, 'Approval', 1, 'Pending', SYSUTCDATETIME())", "UX_ReviewTask_Order_Purpose_Round" },
        { "INSERT orders.ReviewTask (OrderId, Purpose, Round, Status, DeadlineAt) VALUES (@o, 'Approval', 1, 'Approved', SYSUTCDATETIME())", "CK_ReviewTask_Lifecycle" },
        { "INSERT orders.ReviewTask (OrderId, Purpose, Round, Status, DeadlineAt) VALUES (@o, 'Escalation', 1, 'Pending', SYSUTCDATETIME())", "CK_ReviewTask_Purpose" },
        { "INSERT orders.ReviewDecision (DecisionId, ReviewId, OrderId, Outcome, OperatorId, PayloadHash) VALUES (NEWID(), NEWID(), @o, 'Approved', 'op', 0x" + new string('0', 64) + ")", "FK_ReviewDecision_ReviewTask_ReviewId" },
        { "INSERT orders.PendingSignal (OrderId, InstanceId, EventName, CausedBy, State, Attempts) VALUES (@o, 'i', 'e', NEWID(), 'Delivered', 0)", "CK_PendingSignal_" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task Database_RejectsViolationOfInvariant(string statement, string constraint)
    {
        var db = await sql.CreateOrderDbAsync("constraints");
        var orderId = await SeedOrderAsync(db);

        await using var connection = new SqlConnection(db);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(statement, connection);
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@o2", Guid.NewGuid());
        command.Parameters.AddWithValue("@k", "seed-key");

        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedOrderAsync(string db)
    {
        await using var host = new OrderApiHost(db);
        using var client = host.CreateCustomerClient();
        using var response = await Orders.SubmitAsync(client, "seed-key", Orders.Body(Orders.NewCustomer("seed"), ("SKU-1", 1)));
        return (await Orders.AcceptedAsync(response)).OrderId;
    }
}
