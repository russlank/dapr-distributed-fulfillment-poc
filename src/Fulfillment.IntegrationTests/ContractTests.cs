using System.Net;
using System.Net.Http.Json;
using Fulfillment.Contracts.Orders;
using Fulfillment.IntegrationTests.Infrastructure;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>Contract violations are refused before anything is persisted.</summary>
public sealed class ContractTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Sentinel = "SENTINEL-7f3c";

    private string db = "";
    private OrderApiHost host = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        db = await sql.CreateOrderDbAsync("contract");
        host = new OrderApiHost(db);
        client = host.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await host.DisposeAsync();
    }

    public static TheoryData<string, string> InvalidBodies() => new()
    {
        { "not json " + Sentinel, OrderErrorCodes.InvalidJson },
        { "{\"customerId\":\"CUSTOMER\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":1}],\"extra\":\"" + Sentinel + "\"}", OrderErrorCodes.InvalidJson },
        { "{\"customerId\":\"CUSTOMER\",\"customerId\":\"" + Sentinel + "\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":1}]}", OrderErrorCodes.InvalidJson },
        { "{\"customerId\":\"CUSTOMER\"}", OrderErrorCodes.InvalidJson },
        { "{\"customerId\":null,\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":1}]}", OrderErrorCodes.InvalidJson },
        { "{\"CustomerId\":\"CUSTOMER\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":1}]}", OrderErrorCodes.InvalidJson },
        { "{\"customerId\":\"CUSTOMER\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":\"" + Sentinel + "\"}]}", OrderErrorCodes.InvalidJson },
        { "{\"customerId\":\"CUSTOMER\",\"lines\":[]}", OrderErrorCodes.ValidationFailed },
        { "{\"customerId\":\"CUSTOMER\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":0}]}", OrderErrorCodes.ValidationFailed },
        { "{\"customerId\":\"CUSTOMER\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":101}]}", OrderErrorCodes.ValidationFailed },
        { "{\"customerId\":\"CUSTOMER\",\"lines\":[{\"sku\":\"" + Sentinel.ToLowerInvariant() + "\",\"quantity\":1}]}", OrderErrorCodes.ValidationFailed },
        { "{\"customerId\":\"has space " + Sentinel + "\",\"lines\":[{\"sku\":\"SKU-1\",\"quantity\":1}]}", OrderErrorCodes.ValidationFailed },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Submit_ContractViolation_Returns400WithCodeAndPersistsNothing(string template, string code)
    {
        var customer = Orders.NewCustomer("contract");
        var key = Orders.NewKey();

        using var response = await Orders.SubmitAsync(client, key, template.Replace("CUSTOMER", customer, StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"code\":\"{code}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, customer));
        Assert.Equal(0, await Db.CountKeysAsync(db, key));
    }

    [Fact]
    public async Task Submit_TooManyLines_Returns400AndPersistsNothing()
    {
        var customer = Orders.NewCustomer("lines");
        var lines = Enumerable.Range(1, OrderLimits.MaxLines + 1).Select(i => ($"SKU-{i}", 1)).ToArray();

        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(customer, lines), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, customer));
    }

    [Fact]
    public async Task Submit_MaximumLines_IsAccepted()
    {
        var customer = Orders.NewCustomer("maxlines");
        var lines = Enumerable.Range(1, OrderLimits.MaxLines).Select(i => ($"SKU-{i}", OrderLimits.MaxQuantity)).ToArray();

        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(customer, lines), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal((1, OrderLimits.MaxLines, 1, 1, 1), await Db.CountForCustomerAsync(db, customer));
    }

    [Fact]
    public async Task Submit_BodyOverLimit_Returns413()
    {
        var customer = Orders.NewCustomer("large");
        var body = Orders.Body(customer, ("SKU-1", 1)).Replace("{\"customerId\"", new string(' ', OrderLimits.MaxBodyBytes) + "{\"customerId\"", StringComparison.Ordinal);

        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, customer));
    }

    [Theory]
    [InlineData(null, OrderErrorCodes.IdempotencyKeyMissing)]
    [InlineData("has space", OrderErrorCodes.IdempotencyKeyInvalid)]
    public async Task Submit_MissingOrInvalidIdempotencyKey_Returns400AndPersistsNothing(string? key, string code)
    {
        var customer = Orders.NewCustomer("key");

        using var response = await Orders.SubmitAsync(client, key, Orders.Body(customer, ("SKU-1", 1)), TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, problem!["code"].ToString());
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, customer));
    }

    [Fact]
    public async Task Submit_OverlongIdempotencyKey_Returns400()
    {
        using var response = await Orders.SubmitAsync(client, new string('k', OrderLimits.MaxIdempotencyKeyLength + 1),
            Orders.Body(Orders.NewCustomer("longkey"), ("SKU-1", 1)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Orders_WithoutValidApiKey_Return401AndPersistNothing(string? apiKey)
    {
        var customer = Orders.NewCustomer("auth");
        using var anonymous = host.CreateClient();
        if (apiKey is not null)
        {
            anonymous.DefaultRequestHeaders.Add(OrderRoutes.ApiKeyHeader, apiKey);
        }

        using var submit = await Orders.SubmitAsync(anonymous, Orders.NewKey(), Orders.Body(customer, ("SKU-1", 1)), TestContext.Current.CancellationToken);
        using var get = await anonymous.GetAsync($"/orders/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, submit.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal((0, 0, 0, 0, 0), await Db.CountForCustomerAsync(db, customer));
    }
}
