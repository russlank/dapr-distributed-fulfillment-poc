using System.Net;
using Fulfillment.Contracts.Orders;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Orders;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// Forces the race the concurrent tests can only provoke: another submission commits the key after
/// this one's lookup found nothing. A transaction holds an uncommitted key row (invisible to the
/// lookup under read committed snapshot); the submission's insert then blocks on it, and the commit
/// turns that insert into a unique violation, which must resolve like a sequential retry.
/// </summary>
public sealed class KeyRaceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost host = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("race");
        host = new OrderApiHost(db);
        client = host.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await host.DisposeAsync();
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.UnprocessableEntity)]
    public async Task Submit_KeyCommittedByAnotherReplicaAfterLookup_ResolvesThroughUniqueViolation(bool samePayload, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var customer = Orders.NewCustomer("race");
        var key = Orders.NewKey();
        var body = Orders.Body(customer, ("SKU-1", 2));
        var heldRequest = new SubmitOrderRequest(customer, [new SubmitOrderLine("SKU-1", samePayload ? 2 : 3)]);
        var heldOrderId = Guid.CreateVersion7();

        await using var holder = new SqlConnection(db);
        await holder.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(ct);
        await using (var insert = new SqlCommand(
            """
            INSERT orders.[Order] (OrderId, CustomerId, Status) VALUES (@o, @c, 'Submitted');
            INSERT orders.IdempotencyKey ([Key], RequestHash, OrderId) VALUES (@k, @h, @o);
            """, holder, transaction))
        {
            insert.Parameters.AddWithValue("@o", heldOrderId);
            insert.Parameters.AddWithValue("@c", customer);
            insert.Parameters.AddWithValue("@k", key);
            insert.Parameters.AddWithValue("@h", OrderRequests.Hash(heldRequest));
            await insert.ExecuteNonQueryAsync(ct);
        }

        var submission = Orders.SubmitAsync(client, key, body, ct);

        // Proof of the race: the submission is blocked on the held key (lock wait), i.e. its lookup
        // found nothing and its insert is waiting for the other transaction.
        Assert.True(await WaitForLockWaitAsync(TimeSpan.FromSeconds(20), ct), "the submission never blocked on the held key");
        Assert.False(submission.IsCompleted);

        await transaction.CommitAsync(ct);
        using var response = await submission;

        Assert.Equal(expected, response.StatusCode);
        if (samePayload)
        {
            var accepted = await Orders.AcceptedAsync(response);
            Assert.True(accepted.Replayed);
            Assert.Equal(heldOrderId, accepted.OrderId);
        }

        // Only the held order exists; the losing submission rolled back completely.
        Assert.Equal((1, 0, 0, 1, 0), await Db.CountForCustomerAsync(db, customer));
    }

    private async Task<bool> WaitForLockWaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await Db.ScalarAsync<int>(db,
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND blocking_session_id <> 0 AND wait_type LIKE 'LCK%'");
            if (waiting > 0)
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return false;
    }
}
