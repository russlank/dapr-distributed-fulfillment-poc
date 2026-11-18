using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>Acceptance, replay, and key reuse through the real HTTP API, asserted on persisted rows.</summary>
public sealed class OrderAcceptanceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost host = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("accept");
        host = new OrderApiHost(db);
        client = host.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Submit_NewOrder_PersistsOrderLinesHistoryKeyAndPendingStartRequest()
    {
        var customer = Orders.NewCustomer("new");
        var key = Orders.NewKey();

        using var response = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 2), ("SKU-2", 5)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var accepted = await Orders.AcceptedAsync(response);
        Assert.False(accepted.Replayed);
        Assert.Equal("Submitted", accepted.Status);
        Assert.Equal($"/orders/{accepted.OrderId}", response.Headers.Location?.OriginalString);

        var order = (await Db.RowsAsync(db, "SELECT CustomerId, Status FROM orders.[Order] WHERE OrderId = @id", ("@id", accepted.OrderId))).Single();
        Assert.Equal(customer, order["CustomerId"]);
        Assert.Equal("Submitted", order["Status"]);

        var lines = await Db.RowsAsync(db, "SELECT LineNumber, Sku, Quantity FROM orders.OrderLine WHERE OrderId = @id ORDER BY LineNumber", ("@id", accepted.OrderId));
        Assert.Equal([(1, "SKU-1", 2), (2, "SKU-2", 5)], lines.Select(l => ((int)l["LineNumber"]!, (string)l["Sku"]!, (int)l["Quantity"]!)));

        var history = (await Db.RowsAsync(db, "SELECT Sequence, FromStatus, ToStatus, ReasonCode FROM orders.OrderStatusHistory WHERE OrderId = @id", ("@id", accepted.OrderId))).Single();
        Assert.Equal(1, history["Sequence"]);
        Assert.Null(history["FromStatus"]);
        Assert.Equal("Submitted", history["ToStatus"]);
        Assert.Equal("Accepted", history["ReasonCode"]);

        // The key is bound to the hash of the normalized request.
        var stored = (await Db.RowsAsync(db, "SELECT OrderId, RequestHash FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key))).Single();
        Assert.Equal(accepted.OrderId, stored["OrderId"]);
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            "{\"v\":1,\"customerId\":\"" + customer + "\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":2},{\"sku\":\"SKU-2\",\"quantity\":5}]}"));
        Assert.Equal(expectedHash, (byte[])stored["RequestHash"]!);

        // Execution state: one durable start intent, not yet dispatched; no workflow was started.
        var start = (await Db.RowsAsync(db,
            "SELECT InstanceId, State, Attempts, LeaseOwner, StartedAt, ClosedAt, ExecutionClaims, FenceRejections, CASE WHEN NextAttemptAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END AS Due FROM orders.WorkflowStartRequest WHERE OrderId = @id",
            ("@id", accepted.OrderId))).Single();
        Assert.Equal(FulfillmentWorkflow.InstanceId(accepted.OrderId), start["InstanceId"]);
        Assert.Equal("Pending", start["State"]);
        Assert.Equal(0, start["Attempts"]);
        Assert.Null(start["LeaseOwner"]);
        Assert.Null(start["StartedAt"]);
        Assert.Null(start["ClosedAt"]);
        Assert.Equal(0, start["ExecutionClaims"]);
        Assert.Equal(0, start["FenceRejections"]);
        Assert.Equal(1, start["Due"]);
    }

    [Fact]
    public async Task Submit_SameKeySamePayload_ReturnsOriginalOrderAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var customer = Orders.NewCustomer("replay");
        var key = Orders.NewKey();
        using var first = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 1), ("SKU-2", 3)), ct);
        var original = await Orders.AcceptedAsync(first);

        // Same request with other member order and whitespace: the normalized request is identical.
        var reordered = "{ \"lines\" : [ {\"quantity\":1, \"sku\":\"SKU-1\"}, {\"quantity\":3,\"sku\":\"SKU-2\"} ],\n \"customerId\":\"" + customer + "\" }";
        using var second = await Orders.SubmitAsync(client, key, reordered, ct);
        using var third = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 1), ("SKU-2", 3)), ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var replay = await Orders.AcceptedAsync(second);
        Assert.True(replay.Replayed);
        Assert.Equal(original.OrderId, replay.OrderId);
        Assert.Equal(original.OrderId, (await Orders.AcceptedAsync(third)).OrderId);
        Assert.Equal((1, 2, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
    }

    [Fact]
    public async Task Submit_SameKeyDifferentPayload_Returns422AndKeepsOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        var customer = Orders.NewCustomer("reuse");
        var key = Orders.NewKey();
        using var first = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 1)), ct);
        var original = await Orders.AcceptedAsync(first);
        var hashBefore = await Db.ScalarAsync<string>(db, "SELECT CONVERT(varchar(64), RequestHash, 2) FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key));

        using var quantity = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 2)), ct);
        using var lineOrder = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 1), ("SKU-9", 1)), ct);
        var otherCustomer = Orders.NewCustomer("reuse-other");
        using var customerChange = await Orders.SubmitAsync(client, key, Orders.Body(otherCustomer, ("SKU-1", 1)), ct);

        foreach (var response in new[] { quantity, lineOrder, customerChange })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(ct);
            Assert.Equal(OrderErrorCodes.IdempotencyKeyReused, problem!["code"].ToString());
        }

        Assert.Equal((1, 1, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, otherCustomer));
        Assert.Equal(hashBefore, await Db.ScalarAsync<string>(db, "SELECT CONVERT(varchar(64), RequestHash, 2) FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key)));
        Assert.Equal(original.OrderId, await Db.ScalarAsync<Guid>(db, "SELECT OrderId FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key)));
    }

    [Fact]
    public async Task Get_AcceptedOrder_ReturnsBusinessViewWithoutExecutionState()
    {
        var ct = TestContext.Current.CancellationToken;
        var customer = Orders.NewCustomer("get");
        using var submitted = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(customer, ("SKU-7", 4)), ct);
        var accepted = await Orders.AcceptedAsync(submitted);

        using var response = await client.GetAsync($"/orders/{accepted.OrderId}", ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        var view = await response.Content.ReadFromJsonAsync<OrderView>(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(accepted.OrderId, view!.OrderId);
        Assert.Equal(customer, view.CustomerId);
        Assert.Equal("Submitted", view.Status);
        Assert.Equal([new OrderLineView(1, "SKU-7", 4)], view.Lines);
        Assert.Equal(1, Assert.Single(view.History).Sequence);
        Assert.DoesNotContain("instance", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_UnknownOrder_Returns404()
    {
        using var response = await client.GetAsync($"/orders/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
