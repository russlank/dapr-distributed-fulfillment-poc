using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Orders;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>The execution claim against SQL Server: counters, audit rows, and the terminal rejection.</summary>
public sealed class ExecutionFenceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("fence");
        api = new OrderApiHost(db);
        client = api.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await api.DisposeAsync();
    }

    [Fact]
    public async Task Claim_NonTerminalOrder_ProceedsAndCountsEveryClaim()
    {
        var orderId = await AcceptAsync();
        var fence = new ExecutionFence(db);
        var request = new ExecutionClaimRequest(orderId, FulfillmentWorkflow.InstanceId(orderId));

        var first = await fence.ClaimAsync(request, TestContext.Current.CancellationToken);
        var replayed = await fence.ClaimAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(new ExecutionClaimResult(ExecutionClaimOutcomes.Proceed, "Submitted", 1), first);
        Assert.Equal(2, replayed.ExecutionClaims);
        Assert.True(replayed.Proceed);
        Assert.Equal((2, 0), await CountersAsync(orderId));
        Assert.Equal(["Proceed", "Proceed"], (await ClaimsAsync(orderId)).Select(c => c["Outcome"]));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    [InlineData("Compensated")]
    public async Task Claim_TerminalOrder_IsRejectedCountedAndAudited(string status)
    {
        var orderId = await AcceptAsync();
        await Db.ExecuteAsync(db, $"UPDATE orders.[Order] SET Status = '{status}' WHERE OrderId = '{orderId}'");

        var result = await new ExecutionFence(db).ClaimAsync(new ExecutionClaimRequest(orderId, FulfillmentWorkflow.InstanceId(orderId)), TestContext.Current.CancellationToken);

        Assert.False(result.Proceed);
        Assert.Equal(ExecutionClaimOutcomes.AlreadyTerminal, result.Outcome);
        Assert.Equal(status, result.OrderStatus);
        Assert.Equal((0, 1), await CountersAsync(orderId));
        var audit = Assert.Single(await ClaimsAsync(orderId));
        Assert.Equal(("AlreadyTerminal", status, FulfillmentWorkflow.InstanceId(orderId)), ((string)audit["Outcome"]!, (string)audit["OrderStatus"]!, (string)audit["InstanceId"]!));
    }

    [Fact]
    public async Task Claim_ConcurrentClaims_AreAllCounted()
    {
        var orderId = await AcceptAsync();
        var fence = new ExecutionFence(db);
        var request = new ExecutionClaimRequest(orderId, FulfillmentWorkflow.InstanceId(orderId));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => fence.ClaimAsync(request))));

        Assert.Equal(Enumerable.Range(1, 20), results.Select(r => r.ExecutionClaims).Order());
        Assert.Equal((20, 0), await CountersAsync(orderId));
        Assert.Equal(20, (await ClaimsAsync(orderId)).Count);
    }

    [Fact]
    public async Task Claim_UnknownOrder_FailsWithoutWriting()
    {
        var unknown = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExecutionFence(db).ClaimAsync(new ExecutionClaimRequest(unknown, FulfillmentWorkflow.InstanceId(unknown)), TestContext.Current.CancellationToken));

        Assert.Empty(await ClaimsAsync(unknown));
    }

    private async Task<Guid> AcceptAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("fence"), ("SKU-1", 1)));
        return (await Orders.AcceptedAsync(response)).OrderId;
    }

    private async Task<(int Claims, int Rejections)> CountersAsync(Guid orderId)
    {
        var row = (await Db.RowsAsync(db, "SELECT ExecutionClaims, FenceRejections FROM orders.WorkflowStartRequest WHERE OrderId = @o", ("@o", orderId))).Single();
        return ((int)row["ExecutionClaims"]!, (int)row["FenceRejections"]!);
    }

    private async Task<List<Dictionary<string, object?>>> ClaimsAsync(Guid orderId) =>
        await Db.RowsAsync(db, "SELECT Outcome, OrderStatus, InstanceId FROM orders.ExecutionClaim WHERE OrderId = @o ORDER BY Id", ("@o", orderId));
}
