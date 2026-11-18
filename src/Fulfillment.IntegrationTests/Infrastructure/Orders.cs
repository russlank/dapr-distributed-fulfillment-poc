using System.Net.Http.Json;
using System.Text;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>Request helpers: bodies are raw JSON so tests control member order and whitespace.</summary>
public static class Orders
{
    public static string NewCustomer(string test) => $"it-{test}-{Guid.NewGuid():N}"[..Math.Min(test.Length + 36, 64)];

    public static string NewKey() => $"key-{Guid.NewGuid():N}";

    public static string Body(string customerId, params (string Sku, int Quantity)[] lines) =>
        "{\"customerId\":\"" + customerId + "\",\"lines\":["
        + string.Join(",", lines.Select(l => "{\"sku\":\"" + l.Sku + "\",\"quantity\":" + l.Quantity + "}"))
        + "]}";

    public static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, string? key, string body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OrderRoutes.Orders)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add(OrderRoutes.IdempotencyKeyHeader, key);
        }

        return await client.SendAsync(request, ct);
    }

    public static async Task<OrderAccepted> AcceptedAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<OrderAccepted>())!;
}
