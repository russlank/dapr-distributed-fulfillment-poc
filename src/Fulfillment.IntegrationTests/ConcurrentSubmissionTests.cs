using System.Net;
using Fulfillment.IntegrationTests.Infrastructure;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// Concurrent submissions through two independent API hosts (two replicas) on one database. The
/// assertions do not depend on which request wins: exactly one order per key, and every response
/// agrees with what was persisted.
/// </summary>
public sealed class ConcurrentSubmissionTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const int Requests = 24;

    private string db = "";
    private OrderApiHost replicaA = null!;
    private OrderApiHost replicaB = null!;
    private HttpClient clientA = null!;
    private HttpClient clientB = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("concurrent");
        replicaA = new OrderApiHost(db);
        replicaB = new OrderApiHost(db);
        clientA = replicaA.CreateCustomerClient();
        clientB = replicaB.CreateCustomerClient();

        // Warm both hosts (startup, model build, pool) so the requests below actually overlap.
        await Orders.SubmitAsync(clientA, Orders.NewKey(), Orders.Body(Orders.NewCustomer("warm"), ("SKU-1", 1)));
        await Orders.SubmitAsync(clientB, Orders.NewKey(), Orders.Body(Orders.NewCustomer("warm"), ("SKU-1", 1)));
    }

    public async ValueTask DisposeAsync()
    {
        clientA.Dispose();
        clientB.Dispose();
        await replicaA.DisposeAsync();
        await replicaB.DisposeAsync();
    }

    [Fact]
    public async Task Submit_SameKeySamePayloadConcurrentlyOnTwoReplicas_CreatesExactlyOneOrder()
    {
        var customer = Orders.NewCustomer("same");
        var key = Orders.NewKey();
        var body = Orders.Body(customer, ("SKU-1", 2), ("SKU-2", 1));

        var responses = await SubmitTogetherAsync(i => (i % 2 == 0 ? clientA : clientB, key, body));

        Assert.All(responses, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"status {r.Status}"));
        Assert.Single(responses, r => r.Status == HttpStatusCode.Created);
        var orderId = Assert.Single(responses.Select(r => r.OrderId).Distinct());
        Assert.Equal((1, 2, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
        Assert.Equal(orderId, await Db.ScalarAsync<Guid>(db, "SELECT OrderId FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key)));
    }

    [Fact]
    public async Task Submit_SameKeyConflictingPayloadsConcurrently_OneWinsOthersAgreeOrConflict()
    {
        var customer = Orders.NewCustomer("conflict");
        var key = Orders.NewKey();
        var bodyA = Orders.Body(customer, ("SKU-1", 1));
        var bodyB = Orders.Body(customer, ("SKU-1", 9));

        var responses = await SubmitTogetherAsync(i => (i % 2 == 0 ? clientA : clientB, key, i % 4 < 2 ? bodyA : bodyB));

        // Exactly one order; it carries the winning payload. Requests with that payload got its id,
        // the others got 422; none created anything else.
        Assert.Equal((1, 1, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
        var storedQuantity = await Db.ScalarAsync<int>(db,
            "SELECT l.Quantity FROM orders.OrderLine l JOIN orders.[Order] o ON o.OrderId = l.OrderId WHERE o.CustomerId = @c", ("@c", customer));
        var storedOrder = await Db.ScalarAsync<Guid>(db, "SELECT OrderId FROM orders.IdempotencyKey WHERE [Key] = @k", ("@k", key));
        var winnerBody = storedQuantity == 1 ? bodyA : bodyB;

        Assert.Single(responses, r => r.Status == HttpStatusCode.Created);
        foreach (var r in responses)
        {
            if (r.Body == winnerBody)
            {
                Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"winner payload got {r.Status}");
                Assert.Equal(storedOrder, r.OrderId);
            }
            else
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            }
        }
    }

    [Fact]
    public async Task Submit_DistinctKeysConcurrentlyOnTwoReplicas_CreatesOneOrderAndStartRequestEach()
    {
        var customer = Orders.NewCustomer("distinct");
        var keys = Enumerable.Range(0, Requests).Select(_ => Orders.NewKey()).ToArray();

        var responses = await SubmitTogetherAsync(i => (i % 2 == 0 ? clientA : clientB, keys[i], Orders.Body(customer, ("SKU-3", i % 100 + 1))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Equal(Requests, responses.Select(r => r.OrderId).Distinct().Count());
        Assert.Equal((Requests, Requests, Requests, Requests, Requests), await Db.CountForCustomerAsync(db, customer));
        Assert.Equal(Requests, await Db.ScalarAsync<int>(db,
            "SELECT COUNT(DISTINCT r.InstanceId) FROM orders.WorkflowStartRequest r JOIN orders.[Order] o ON o.OrderId = r.OrderId WHERE o.CustomerId = @c AND r.State = 'Pending'",
            ("@c", customer)));
    }

    private static async Task<List<(HttpStatusCode Status, Guid? OrderId, string Body)>> SubmitTogetherAsync(
        Func<int, (HttpClient Client, string Key, string Body)> request)
    {
        using var start = new Barrier(Requests);
        var tasks = Enumerable.Range(0, Requests).Select(i => Task.Run(async () =>
        {
            var (client, key, body) = request(i);
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            using var response = await Orders.SubmitAsync(client, key, body);
            Guid? orderId = response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK
                ? (await Orders.AcceptedAsync(response)).OrderId
                : null;
            return (response.StatusCode, orderId, body);
        })).ToList();
        return [.. await Task.WhenAll(tasks)];
    }
}
