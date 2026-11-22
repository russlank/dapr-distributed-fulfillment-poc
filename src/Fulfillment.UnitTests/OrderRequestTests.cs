using System.Text;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Fulfillment.OrderApi.Orders;
using Xunit;

namespace Fulfillment.UnitTests;

public class OrderRequestTests
{
    private static SubmitOrderRequest Parse(string json)
    {
        var request = OrderRequests.TryParse(Encoding.UTF8.GetBytes(json), out var path);
        Assert.True(request is not null, $"not parsed (at {path})");
        return request;
    }

    [Fact]
    public void Hash_SameRequestWithOtherMemberOrderAndWhitespace_IsEqual()
    {
        var a = Parse("{\"customerId\":\"c-1\",\"lines\":[{\"sku\":\"A\",\"quantity\":1},{\"sku\":\"B\",\"quantity\":2}]}");
        var b = Parse("{ \"lines\": [ { \"quantity\": 1, \"sku\": \"A\" }, {\"quantity\":2,\"sku\":\"B\"} ],\n  \"customerId\": \"c-1\" }");

        Assert.Equal(OrderRequests.Hash(a), OrderRequests.Hash(b));
        Assert.Equal(32, OrderRequests.Hash(a).Length);
    }

    [Theory]
    [InlineData("{\"customerId\":\"c-2\",\"lines\":[{\"sku\":\"A\",\"quantity\":1},{\"sku\":\"B\",\"quantity\":2}]}")]
    [InlineData("{\"customerId\":\"c-1\",\"lines\":[{\"sku\":\"B\",\"quantity\":2},{\"sku\":\"A\",\"quantity\":1}]}")]
    [InlineData("{\"customerId\":\"c-1\",\"lines\":[{\"sku\":\"A\",\"quantity\":1},{\"sku\":\"B\",\"quantity\":3}]}")]
    [InlineData("{\"customerId\":\"c-1\",\"lines\":[{\"sku\":\"A\",\"quantity\":1}]}")]
    public void Hash_DifferentCustomerLineOrderQuantityOrLines_Differs(string other)
    {
        var original = Parse("{\"customerId\":\"c-1\",\"lines\":[{\"sku\":\"A\",\"quantity\":1},{\"sku\":\"B\",\"quantity\":2}]}");

        Assert.NotEqual(OrderRequests.Hash(original), OrderRequests.Hash(Parse(other)));
    }

    [Theory]
    [InlineData("{\"customerId\":\"c\",\"lines\":[],\"extra\":1}", "$.extra")]
    [InlineData("{\"customerId\":\"c\",\"customerId\":\"d\",\"lines\":[]}", "$.customerId")]
    [InlineData("{\"customerId\":\"c\"}", "$")]
    [InlineData("{\"customerId\":null,\"lines\":[]}", "$.customerId")]
    [InlineData("{\"CustomerId\":\"c\",\"lines\":[]}", "$.CustomerId")]
    [InlineData("[1,2]", "$")]
    [InlineData("", "$")]
    public void TryParse_ContractViolation_ReturnsNullWithPathOnly(string json, string expectedPathPrefix)
    {
        var request = OrderRequests.TryParse(Encoding.UTF8.GetBytes(json), out var path);

        Assert.Null(request);
        Assert.NotNull(path);
        Assert.StartsWith(expectedPathPrefix, path, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_BoundaryValues_AreAccepted()
    {
        var lines = Enumerable.Range(1, OrderLimits.MaxLines).Select(i => new SubmitOrderLine("SKU-" + i, i % 2 == 0 ? OrderLimits.MaxQuantity : OrderLimits.MinQuantity)).ToList();

        Assert.Empty(OrderRequests.Validate(new SubmitOrderRequest(new string('c', OrderLimits.MaxCustomerIdLength), lines)));
        Assert.Empty(OrderRequests.Validate(new SubmitOrderRequest("c", [new SubmitOrderLine(new string('S', OrderLimits.MaxSkuLength), 1)])));
    }

    [Theory]
    [InlineData("", "SKU", 1, "customerId")]
    [InlineData("has space", "SKU", 1, "customerId")]
    [InlineData("c", "sku", 1, "lines[0].sku")]
    [InlineData("c", "-SKU", 1, "lines[0].sku")]
    [InlineData("c", "SKU", 0, "lines[0].quantity")]
    [InlineData("c", "SKU", 101, "lines[0].quantity")]
    public void Validate_OutOfBounds_ReportsField(string customerId, string sku, int quantity, string field)
    {
        var errors = OrderRequests.Validate(new SubmitOrderRequest(customerId, [new SubmitOrderLine(sku, quantity)]));

        Assert.Contains(field, errors.Keys);
    }

    [Fact]
    public void Validate_NoLinesOrTooMany_ReportsLines()
    {
        var tooMany = Enumerable.Range(0, OrderLimits.MaxLines + 1).Select(_ => new SubmitOrderLine("SKU", 1)).ToList();

        Assert.Contains("lines", OrderRequests.Validate(new SubmitOrderRequest("c", [])).Keys);
        Assert.Contains("lines", OrderRequests.Validate(new SubmitOrderRequest("c", tooMany)).Keys);
    }

    [Fact]
    public void Validate_Messages_NeverQuoteValues()
    {
        const string sentinel = "sentinel value 42";
        var errors = OrderRequests.Validate(new SubmitOrderRequest(sentinel, [new SubmitOrderLine(sentinel, 4242)]));

        Assert.NotEmpty(errors);
        Assert.All(errors.Values.SelectMany(v => v), m => Assert.DoesNotContain("sentinel", m, StringComparison.Ordinal));
        Assert.All(errors.Values.SelectMany(v => v), m => Assert.DoesNotContain("4242", m, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("k", true)]
    [InlineData("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("with space", false)]
    [InlineData("tab\t", false)]
    [InlineData("ümlaut", false)]
    public void IsValidIdempotencyKey_ChecksVisibleAscii(string? key, bool valid)
    {
        Assert.Equal(valid, OrderRequests.IsValidIdempotencyKey(key));
    }

    [Fact]
    public void IsValidIdempotencyKey_LongerThanLimit_IsRejected()
    {
        Assert.True(OrderRequests.IsValidIdempotencyKey(new string('k', OrderLimits.MaxIdempotencyKeyLength)));
        Assert.False(OrderRequests.IsValidIdempotencyKey(new string('k', OrderLimits.MaxIdempotencyKeyLength + 1)));
    }
}

public class OrderModelTests
{
    [Fact]
    public void Submit_NumbersLinesInRequestOrderAndRecordsFirstStatus()
    {
        var orderId = Guid.Parse("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b");

        var order = Order.Submit(orderId, "c-1", [("B", 2), ("A", 1)]);

        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Equal([(1, "B", 2), (2, "A", 1)], order.Lines.Select(l => (l.LineNumber, l.Sku, l.Quantity)));
        var first = Assert.Single(order.History);
        Assert.Equal((1, (OrderStatus?)null, OrderStatus.Submitted, OrderReasonCodes.Accepted), (first.Sequence, first.FromStatus, first.ToStatus, first.ReasonCode));
    }

    [Fact]
    public void StartRequest_ForNewOrder_IsPendingWithDerivedInstanceId()
    {
        var orderId = Guid.Parse("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b");

        var request = WorkflowStartRequest.ForNewOrder(orderId);

        Assert.Equal(WorkflowStartState.Pending, request.State);
        Assert.Equal("fulfill-3f2b8c1e5a4d4e6f9a1b2c3d4e5f6a7b", request.InstanceId);
        Assert.Equal(FulfillmentWorkflow.InstanceId(orderId), request.InstanceId);
        Assert.Equal((0, 0, 0), (request.Attempts, request.ExecutionClaims, request.FenceRejections));
    }

    [Theory]
    [InlineData(OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Rejected, true)]
    [InlineData(OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Compensated, true)]
    [InlineData(OrderStatus.Escalated, true)]
    [InlineData(OrderStatus.Submitted, false)]
    [InlineData(OrderStatus.AwaitingDelivery, false)]
    [InlineData(OrderStatus.ManualInterventionRequired, false)]
    public void IsTerminal_MatchesTheFulfillmentFence(OrderStatus status, bool terminal)
    {
        Assert.Equal(terminal, OrderStatuses.IsTerminal(status));
    }
}

public class OrderSchemaTests
{
    // A model change without a migration would leave the database behind the code; EF compares the
    // model with the migrations' snapshot without connecting.
    [Fact]
    public void OrderDbModel_HasNoChangesMissingFromMigrations()
    {
        using var db = new Fulfillment.OrderApi.Persistence.OrderDbDesignTimeFactory().CreateDbContext([]);

        Assert.False(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.HasPendingModelChanges(db.Database));
    }
}
