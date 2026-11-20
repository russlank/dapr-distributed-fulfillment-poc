using System.Net;
using System.Net.Http.Json;
using Fulfillment.Contracts.Reviews;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// Manual escalation of a review round against SQL Server: the round becomes Escalated, the next round opens with a
/// fresh deadline, and the waiter of the old round is signalled, in one transaction; a repeat returns the same next
/// round; the workflow's expiry step follows it; a decision for the escalated round is superseded; the last round
/// cannot be escalated.
/// </summary>
public sealed class EscalationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string db = "";
    private OrderApiHost api = null!;
    private HttpClient customer = null!;
    private HttpClient operatorClient = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("escalate");
        api = new OrderApiHost(db);
        customer = api.CreateCustomerClient();
        operatorClient = api.CreateOperatorClient();
    }

    public async ValueTask DisposeAsync()
    {
        customer.Dispose();
        operatorClient.Dispose();
        await api.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> EscalateAsync(Guid reviewId) =>
        operatorClient.PostAsJsonAsync($"/reviews/{reviewId}/escalate", new EscalateReviewRequest("operator-2", "needs a senior decision"), Ct);

    [Fact]
    public async Task Escalate_OpensTheNextRound_SignalsTheWaiter_RepeatReturnsTheSameRound_OldDecisionSuperseded()
    {
        using var response = await Orders.SubmitAsync(customer, Orders.NewKey(), Orders.Body(Orders.NewCustomer("escalate"), ("SKU-1", 1)), Ct);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;
        var round1 = await Reviewing.OpenAsync(db, orderId, ct: Ct);

        using var first = await EscalateAsync(round1.ReviewId);
        var escalated = await first.Content.ReadFromJsonAsync<ReviewEscalated>(Ct);
        using var again = await EscalateAsync(round1.ReviewId);
        var replay = await again.Content.ReadFromJsonAsync<ReviewEscalated>(Ct);
        var followed = await Reviewing.Steps(db).ExpireOrEscalateAsync(new ExpireReviewRequest(orderId, round1.ReviewId), Ct);
        using var oldDecision = await Reviewing.DecideAsync(operatorClient, round1.ReviewId, Reviewing.Decision(), Ct);
        using var lastRound = await EscalateAsync(escalated!.NextReviewId);

        Assert.Equal((HttpStatusCode.OK, 2, false), (first.StatusCode, escalated.NextRound, escalated.Replayed));
        Assert.Equal((escalated.NextReviewId, true), (replay!.NextReviewId, replay.Replayed));
        Assert.Equal((ExpiryOutcomes.NextRound, escalated.NextReviewId), (followed.Outcome, followed.NextRound!.ReviewId));
        Assert.Equal((HttpStatusCode.Conflict, ReviewErrorCodes.ReviewSuperseded), (oldDecision.StatusCode, await Reviewing.CodeAsync(oldDecision)));
        Assert.Equal((HttpStatusCode.Conflict, ReviewErrorCodes.NoFurtherRound), (lastRound.StatusCode, await Reviewing.CodeAsync(lastRound)));
        Assert.Equal("Escalated|Pending|1|AwaitingApproval", await Db.ScalarAsync<string>(db,
            """
            SELECT CONCAT((SELECT Status FROM orders.ReviewTask WHERE ReviewId = @r1), '|', (SELECT Status FROM orders.ReviewTask WHERE ReviewId = @r2), '|',
              (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o AND EventName = @n), '|', (SELECT Status FROM orders.[Order] WHERE OrderId = @o))
            """, ("@r1", round1.ReviewId), ("@r2", escalated.NextReviewId), ("@o", orderId), ("@n", ReviewSignals.EventName(round1.ReviewId))));
    }
}
