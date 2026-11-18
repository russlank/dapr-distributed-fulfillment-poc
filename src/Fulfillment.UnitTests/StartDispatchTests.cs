using System.Net;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Messaging;
using Fulfillment.OrderApi.Dispatch;
using Fulfillment.WorkflowHost;
using Xunit;

namespace Fulfillment.UnitTests;

public class StartDispatchTests
{
    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(4, 8000)]
    [InlineData(7, 60000)]
    [InlineData(100, 60000)]
    public void Backoff_DoublesFromBaseAndCaps(int attempt, int expectedMs)
    {
        var delay = WorkflowStartDispatcher.Backoff(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), jitter: 0);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    [Fact]
    public void Backoff_JitterAddsAtMostTwentyPercent()
    {
        var max = WorkflowStartDispatcher.Backoff(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), jitter: 1);

        Assert.Equal(TimeSpan.FromMilliseconds(4800), max);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"instanceId\":\"fulfill-x\",\"started\":true,\"status\":\"Scheduled\"}", StartOutcomeKind.Started, "Scheduled")]
    [InlineData(HttpStatusCode.OK, "{\"instanceId\":\"fulfill-x\",\"started\":false,\"status\":\"Completed\"}", StartOutcomeKind.AlreadyExists, "Completed")]
    [InlineData(HttpStatusCode.Forbidden, "{}", StartOutcomeKind.Rejected, "403")]
    [InlineData(HttpStatusCode.BadRequest, "{}", StartOutcomeKind.Rejected, "400")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", StartOutcomeKind.Ambiguous, "500")]
    [InlineData(null, "", StartOutcomeKind.Ambiguous, null)]
    public async Task WorkflowHostStarter_ClassifiesTheWorkflowHostAnswer(HttpStatusCode? status, string body, StartOutcomeKind kind, string? reported)
    {
        var handler = new FixedHandler(status, body);
        var starter = new WorkflowHostStarter(new SidecarInvoker(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:3500/") },
            delay: (_, _) => Task.CompletedTask));

        var outcome = await starter.StartAsync("fulfill-x", new FulfillmentInput(Guid.Empty), TestContext.Current.CancellationToken);

        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(reported, outcome.ReportedStatus);
        // Always the workflow host's management API, never the caller's own workflow API.
        Assert.All(handler.Paths, p => Assert.Equal("v1.0/invoke/workflow-host/method/internal/workflows/FulfillOrderWorkflow/fulfill-x/start", p));
        Assert.All(handler.Keys, k => Assert.Equal("start:fulfill-x", k));
    }

    [Fact]
    public void DefaultPolicy_AdmitsTheFulfillmentWorkflowButNeverItsReplacement()
    {
        var policy = WorkflowManagementPolicy.Default;

        Assert.Equal(ManagementDecision.Allowed, policy.CanStart(AppIds.OrderApi, FulfillmentWorkflow.Name, "fulfill-1", replaceTerminal: false));
        Assert.Equal(ManagementDecision.ReplacementNotAllowed, policy.CanStart(AppIds.OrderApi, FulfillmentWorkflow.Name, "fulfill-1", replaceTerminal: true));
        Assert.Equal(ManagementDecision.CallerNotAuthorized, policy.CanStart(AppIds.InventoryService, FulfillmentWorkflow.Name, "fulfill-1", replaceTerminal: false));
        Assert.Equal(ManagementDecision.Allowed, policy.CanAccess(AppIds.OrderApi, FulfillmentWorkflow.Name));
    }

    private sealed class FixedHandler(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public List<string?> Keys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery.TrimStart('/'));
            Keys.Add(request.Headers.TryGetValues(SidecarInvoker.IdempotencyHeader, out var v) ? v.Single() : null);
            return status is null
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(new HttpResponseMessage(status.Value) { Content = new StringContent(body) });
        }
    }
}
