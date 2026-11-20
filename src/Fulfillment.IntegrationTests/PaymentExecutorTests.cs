extern alias payment;
extern alias provider;
using Fulfillment.Contracts.Payments;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using payment::Fulfillment.PaymentService.Payments;
using payment::Fulfillment.PaymentService.Persistence;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>The real provider simulator (its Program, endpoints, and database) hosted in-process.</summary>
public sealed class ProviderHost(string connectionString) : WebApplicationFactory<provider::Program>
{
    public const string ApiKey = "integration-provider-key";

    static ProviderHost() => Environment.SetEnvironmentVariable(ProviderRoutes.ApiKeyVariable, ApiKey);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("ConnectionStrings:PaymentProviderSimDb", connectionString);
        builder.UseSetting("REPLICA_NAME", "provider-test");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Provider:AuthorizationValidity", "00:10:00");
    }
}

/// <summary>
/// The payment executor against the real provider (in-process) and a real PaymentDb: intent before the request,
/// replay of a finished operation, a response lost after the provider committed (adopted by status query), an
/// unreachable provider (Unavailable, nothing committed), and an undeterminable status (Ambiguous). Assertions read
/// both databases.
/// </summary>
public sealed class PaymentExecutorTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly PaymentOptions Fast = new()
    {
        RequestTimeout = TimeSpan.FromSeconds(2),
        MaxSends = 3,
        SendBackoff = TimeSpan.FromMilliseconds(50),
        StatusQueries = 3,
        StatusQueryInterval = TimeSpan.FromMilliseconds(50),
        IdempotencyWindow = TimeSpan.FromMilliseconds(200),
    };

    private static readonly string[] LostAnswerShapes = ["Send:Timeout,Status:Committed", "Send:Error,Status:Committed"];

    private string providerDb = "";
    private string paymentDb = "";
    private ProviderHost host = null!;

    public async ValueTask InitializeAsync()
    {
        providerDb = await sql.CreateProviderDbAsync("prov");
        paymentDb = await sql.CreateDatabaseAsync("pay");
        var options = new DbContextOptionsBuilder<PaymentDbContext>();
        PaymentDbContext.Configure(options, paymentDb);
        await using (var db = new PaymentDbContext(options.Options))
        {
            await db.Database.MigrateAsync();
        }

        host = new ProviderHost(providerDb);
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PaymentExecutor Executor(HttpClient? client = null)
    {
        client ??= host.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", ProviderHost.ApiKey);
        return new PaymentExecutor(paymentDb, new ProviderClient(client), Fast, "payment-test", NullLogger<PaymentExecutor>.Instance);
    }

    private static PaymentOperationRequest Authorize(Guid order) => new(order, PaymentSteps.Authorize, PaymentSteps.First, 5_000, "EUR");

    private Task<int> ProviderCountAsync(string key) => Db.ScalarAsync<int>(providerDb, "SELECT COUNT(*) FROM provider.Operation WHERE OperationKey = @k", ("@k", key));

    private Task<string> AnswersAsync(string key) => Db.ScalarAsync<string>(paymentDb,
        "SELECT ISNULL(STRING_AGG(CONCAT(Kind, ':', ISNULL(Answer, '-')), ',') WITHIN GROUP (ORDER BY Id), '') FROM payment.PaymentAttempt WHERE OperationKey = @k", ("@k", key));

    [Fact]
    public async Task Execute_Authorize_RecordsTheIntentAndTheOutcome_ARepeatIsAReplay()
    {
        var order = Guid.NewGuid();
        var first = await Executor().ExecuteAsync(Authorize(order), Ct);
        var again = await Executor().ExecuteAsync(Authorize(order), Ct);

        Assert.Equal((PaymentOutcomes.Authorized, false, 1), (first.Outcome, first.Replayed, first.Attempts));
        Assert.NotNull(first.AuthorizationExpiresAtUtc);
        Assert.Equal((PaymentOutcomes.Authorized, true, 1), (again.Outcome, again.Replayed, again.Attempts));
        Assert.Equal(1, await ProviderCountAsync(first.OperationKey));
        Assert.Equal("Send:Committed", await AnswersAsync(first.OperationKey));
    }

    [Fact]
    public async Task Execute_ResponseLostAfterTheProviderCommitted_IsAdoptedByStatusQuery_NoSecondEffect()
    {
        var order = Guid.NewGuid();
        var key = PaymentSteps.Key(order, PaymentSteps.Authorize, PaymentSteps.First);
        await Db.ExecuteAsync(providerDb, "INSERT provider.Fault (Label, Match, Mode, RemainingUses) VALUES ('t', @k, 'DropResponseAfterCommit', 1)", ("@k", key));

        var result = await Executor().ExecuteAsync(Authorize(order), Ct);

        Assert.Equal(PaymentOutcomes.Authorized, result.Outcome);
        Assert.Equal((1, 1), (result.Attempts, result.StatusQueries));
        // The request may have committed (no answer, or the connection broke): a status query, never a blind resend.
        Assert.Contains(await AnswersAsync(key), LostAnswerShapes);
        Assert.Equal(1, await ProviderCountAsync(key));
        Assert.Equal("DropResponseAfterCommit", await Db.ScalarAsync<string>(providerDb, "SELECT FaultApplied FROM provider.Operation WHERE OperationKey = @k", ("@k", key)));
    }

    [Fact]
    public async Task Execute_ProviderUnreachable_IsUnavailable_NothingCommitted_ThenAVoidProhibitsTheKey()
    {
        var order = Guid.NewGuid();
        // Nothing listens on port 9: every connection is refused, so no request can reach a provider.
        using var nowhere = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(500) }) { BaseAddress = new Uri("http://127.0.0.1:9") };

        var result = await Executor(nowhere).ExecuteAsync(Authorize(order), Ct);
        var voided = await Executor().ExecuteAsync(
            new PaymentOperationRequest(order, PaymentSteps.Void, PaymentSteps.First, 5_000, "EUR", PaymentSteps.Authorize, PaymentSteps.First, true), Ct);
        var late = await host.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Post, ProviderRoutes.Operations)
        {
            Headers = { { "X-Api-Key", ProviderHost.ApiKey } },
            Content = System.Net.Http.Json.JsonContent.Create(new ProviderOperationRequest(result.OperationKey, ProviderKinds.Authorize, 5_000, "EUR", null)),
        }, Ct);
        var lateBody = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<ProviderOperationResponse>(late.Content, Ct);

        var detail = await Db.ScalarAsync<string>(paymentDb, "SELECT STRING_AGG(CONCAT(Kind, ':', Answer, ':', Detail), ' | ') FROM payment.PaymentAttempt WHERE OperationKey = @k", ("@k", result.OperationKey));
        Assert.True(result.Outcome == PaymentOutcomes.Unavailable, detail);
        Assert.Equal((PaymentOutcomes.Unavailable, "ProviderUnreachable", 3), (result.Outcome, result.Code, result.Attempts));
        Assert.Equal("Send:Refused,Send:Refused,Send:Refused", await AnswersAsync(result.OperationKey));
        Assert.Contains("connect", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PaymentOutcomes.NothingToCompensate, voided.Outcome);
        Assert.Equal(ProviderStatuses.Prohibited, lateBody!.Status);
        Assert.Equal(0, await ProviderCountAsync(result.OperationKey));
    }

    [Fact]
    public async Task Execute_StatusUndeterminable_IsAmbiguous_ThenReconcileAdoptsTheRecordOnceKnown()
    {
        var order = Guid.NewGuid();
        var key = PaymentSteps.Key(order, PaymentSteps.Authorize, PaymentSteps.First);
        await Db.ExecuteAsync(providerDb,
            "INSERT provider.Fault (Label, Match, Mode, RemainingUses) VALUES ('t', @k, 'DropResponseAfterCommit', 1), ('t', @k, 'StatusUnknown', NULL)", ("@k", key));

        var result = await Executor().ExecuteAsync(Authorize(order), Ct);
        var stillUnknown = await Executor().ReconcileFinishedAsync(Authorize(order), Ct);
        await Db.ExecuteAsync(providerDb, "DELETE provider.Fault WHERE Label = 't'");
        var reconciled = await Executor().ReconcileFinishedAsync(Authorize(order), Ct);

        Assert.Equal((PaymentOutcomes.Ambiguous, "StatusUndetermined"), (result.Outcome, result.Code));
        Assert.Equal(PaymentOutcomes.Ambiguous, stillUnknown.Outcome);
        Assert.Equal(PaymentOutcomes.Authorized, reconciled.Outcome);
        Assert.Equal(1, await ProviderCountAsync(key));
        Assert.Equal(1, await Db.ScalarAsync<int>(paymentDb, "SELECT COUNT(*) FROM payment.PaymentAttempt WHERE OperationKey = @k AND Kind = 'Send'", ("@k", key)));
    }
}
