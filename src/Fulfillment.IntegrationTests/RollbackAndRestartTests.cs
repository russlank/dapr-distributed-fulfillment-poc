using System.Data.Common;
using System.Net;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.OrderApi.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>Atomicity of the acceptance transaction and durability across an application restart.</summary>
public sealed class RollbackAndRestartTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";

    public async ValueTask InitializeAsync() => db = await sql.CreateOrderDbAsync("atomic");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Submit_FailureAfterInsertsBeforeCommit_RollsBackEveryRowAndFreesTheKey()
    {
        var customer = Orders.NewCustomer("rollback");
        var key = Orders.NewKey();
        var body = Orders.Body(customer, ("SKU-1", 1), ("SKU-2", 2));
        var fault = new FailAfterStartRequestInsert();

        await using (var faulty = new OrderApiHost(db, s => s.ConfigureDbContext<OrderDbContext>(o => o.AddInterceptors(fault))))
        {
            using var client = faulty.CreateCustomerClient();
            using var response = await Orders.SubmitAsync(client, key, body, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        // The inserts ran (proof that the failure was inside the transaction), then all were rolled back.
        Assert.True(fault.Fired);
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, customer));
        Assert.Equal(0, await Db.CountKeysAsync(db, key));

        // The key was never consumed: the client's retry is accepted as a new order.
        await using var healthy = new OrderApiHost(db);
        using var retryClient = healthy.CreateCustomerClient();
        using var retry = await Orders.SubmitAsync(retryClient, key, body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((1, 2, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
    }

    [Fact]
    public async Task Submit_RetryAfterApplicationRestart_ReturnsTheOrderFromTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var customer = Orders.NewCustomer("restart");
        var key = Orders.NewKey();
        var body = Orders.Body(customer, ("SKU-5", 3));
        Guid orderId;

        await using (var before = new OrderApiHost(db))
        {
            using var client = before.CreateCustomerClient();
            using var response = await Orders.SubmitAsync(client, key, body, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            orderId = (await Orders.AcceptedAsync(response)).OrderId;
        }

        // Nothing survives in the process: new host, new DI container, no pooled connections.
        SqlConnection.ClearAllPools();

        await using var after = new OrderApiHost(db);
        using var restarted = after.CreateCustomerClient();
        using var replay = await Orders.SubmitAsync(restarted, key, body, ct);
        using var view = await restarted.GetAsync($"/orders/{orderId}", ct);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await Orders.AcceptedAsync(replay);
        Assert.True(replayed.Replayed);
        Assert.Equal(orderId, replayed.OrderId);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal((1, 1, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
        Assert.Equal("Pending", await Db.ScalarAsync<string>(db, "SELECT State FROM orders.WorkflowStartRequest WHERE OrderId = @id", ("@id", orderId)));
    }

    /// <summary>Fails the save after the batch with the start-request insert has executed, before the commit.</summary>
    private sealed class FailAfterStartRequestInsert : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.CommandText.Contains("INSERT INTO [orders].[WorkflowStartRequest]", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException("injected failure before commit");
            }

            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.CommandText.Contains("INSERT INTO [orders].[WorkflowStartRequest]", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException("injected failure before commit");
            }

            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
