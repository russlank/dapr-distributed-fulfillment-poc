using System.Net;
using System.Net.Http.Json;

namespace Fulfillment.Messaging;

/// <summary>Retry policy for one invocation; attempts include the first call.</summary>
public sealed record InvocationRetryPolicy(int MaxAttempts, TimeSpan FirstDelay, TimeSpan MaxDelay)
{
    public static InvocationRetryPolicy Default { get; } = new(4, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));

    public TimeSpan DelayBefore(int attempt)
    {
        // attempt 2 waits FirstDelay, then doubles, capped at MaxDelay.
        var factor = Math.Pow(2, Math.Max(0, attempt - 2));
        var delay = TimeSpan.FromMilliseconds(FirstDelay.TotalMilliseconds * factor);
        return delay < MaxDelay ? delay : MaxDelay;
    }
}

public sealed record InvocationResult(HttpStatusCode? Status, string Body, int Attempts, string? TransportError)
{
    public bool IsSuccess => Status is { } s && (int)s is >= 200 and < 300;
}

/// <summary>
/// Dapr service invocation through the sidecar HTTP API with caller-side bounded retries.
/// The Dapr 1.19 compatibility check showed that a resiliency policy does not retry
/// application 5xx responses, so callers retry themselves. Only 5xx and transport failures
/// are retried; 4xx is a definitive answer. Every attempt carries the same idempotency key,
/// so the receiver must treat repeats as duplicates.
/// </summary>
public sealed class SidecarInvoker(HttpClient http, InvocationRetryPolicy? policy = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string IdempotencyHeader = "Idempotency-Key";

    private readonly InvocationRetryPolicy policy = policy ?? InvocationRetryPolicy.Default;
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    public Task<InvocationResult> PostAsync<T>(string appId, string method, T body, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return SendAsync(appId, method, () => new HttpRequestMessage(HttpMethod.Post, Route(appId, method))
        {
            Content = JsonContent.Create(body),
            Headers = { { IdempotencyHeader, idempotencyKey } },
        }, ct);
    }

    /// <summary>GET is retried the same way; it must be side-effect free on the receiver.</summary>
    public Task<InvocationResult> GetAsync(string appId, string method, CancellationToken ct = default) =>
        SendAsync(appId, method, () => new HttpRequestMessage(HttpMethod.Get, Route(appId, method)), ct);

    private static Uri Route(string appId, string method) => new($"v1.0/invoke/{appId}/method/{method}", UriKind.Relative);

    private async Task<InvocationResult> SendAsync(string appId, string method, Func<HttpRequestMessage> newRequest, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        InvocationResult last = new(null, string.Empty, 0, "not attempted");
        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await delay(policy.DelayBefore(attempt), ct).ConfigureAwait(false);
            }

            using var request = newRequest();
            try
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                last = new InvocationResult(response.StatusCode, text, attempt, null);
                if ((int)response.StatusCode < 500)
                {
                    return last;
                }
            }
            catch (HttpRequestException exception)
            {
                last = new InvocationResult(null, string.Empty, attempt, exception.Message);
            }
        }

        return last;
    }
}
