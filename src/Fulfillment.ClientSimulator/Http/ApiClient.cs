using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.ClientSimulator.Http;

/// <summary>One order API replica: a display name, its base URL, and the client that reaches it.</summary>
public sealed record ApiEndpoint(string Name, Uri BaseUrl, HttpClient Http);

public enum ApiRole
{
    Customer,
    Operator,
    Admin,
}

/// <summary>
/// Role keys, read from the environment only (never from the command line, where other processes
/// can see them). They are sent as headers and never written to output: see <see cref="Redactor"/>.
/// </summary>
public sealed class Credentials(string? customer, string? @operator, string? admin)
{
    public const string CustomerVariable = "CUSTOMER_API_KEY";
    public const string OperatorVariable = "OPERATOR_API_KEY";
    public const string AdminVariable = "ADMIN_API_KEY";

    public static Credentials FromEnvironment() => new(
        Environment.GetEnvironmentVariable(CustomerVariable),
        Environment.GetEnvironmentVariable(OperatorVariable),
        Environment.GetEnvironmentVariable(AdminVariable));

    public Redactor Redactor { get; } = new([customer, @operator, admin]);

    public string? KeyOf(ApiRole role) => role switch
    {
        ApiRole.Customer => customer,
        ApiRole.Operator => @operator,
        _ => admin,
    } is { Length: > 0 } key ? key : null;

    public static string VariableOf(ApiRole role) => role switch
    {
        ApiRole.Customer => CustomerVariable,
        ApiRole.Operator => OperatorVariable,
        _ => AdminVariable,
    };
}

/// <summary>Replaces every configured secret in text that leaves the process (output, errors, logs).</summary>
public sealed class Redactor(IEnumerable<string?> secrets)
{
    public const string Mask = "***";
    private readonly string[] secrets = [.. secrets.Where(s => s is { Length: >= 4 }).Select(s => s!).OrderByDescending(s => s.Length)];

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }
}

/// <param name="MaxAttempts">Attempts per logical call (1 = no retry).</param>
/// <param name="RequestTimeout">Per attempt; a timeout is an ambiguous outcome.</param>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan RequestTimeout, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static RetryPolicy Default { get; } = new(4, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));

    public TimeSpan DelayBefore(int attempt) =>
        attempt <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << Math.Min(attempt - 2, 20))));
}

/// <summary>How an attempt ended.</summary>
public static class AttemptClasses
{
    /// <summary>A definite answer (2xx, or a 4xx refusal): never retried.</summary>
    public const string Answered = "answered";

    /// <summary>No definite answer (timeout, transport error, 408/425/429/5xx): the request may or may not have taken effect.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>Not sent: the role key is not configured.</summary>
    public const string NotSent = "not-sent";

    /// <summary>
    /// Whether a status leaves the outcome open. A retry is safe only because every write carries
    /// its idempotency key (orders) or decision id (reviews) and is resent unchanged.
    /// </summary>
    public static bool IsAmbiguous(int status) => status is 408 or 425 or 429 or >= 500;
}

public sealed record AttemptRecord(int Attempt, string Endpoint, int? HttpStatus, string Class, string? Code, long ElapsedMs, string? Error);

/// <summary>The final answer of a logical call (the last attempt) with every attempt made.</summary>
public sealed record CallResult(int? Status, string? Body, string? Code, IReadOnlyList<AttemptRecord> Attempts)
{
    /// <summary>No definite answer after every attempt: the effect is unknown.</summary>
    public bool Unresolved => Attempts.Count > 0 && Attempts[^1].Class != AttemptClasses.Answered;

    public bool AnyAmbiguous => Attempts.Any(a => a.Class == AttemptClasses.Ambiguous);

    public string FinalEndpoint => Attempts.Count > 0 ? Attempts[^1].Endpoint : "";
}

/// <summary>
/// HTTP to the order API replicas. A call goes to its first endpoint; after an ambiguous attempt it
/// is resent, byte for byte with the same key, to the next endpoint (or the same one when pinned).
/// </summary>
public sealed class ApiClient(IReadOnlyList<ApiEndpoint> endpoints, Credentials credentials, RetryPolicy policy)
{
    public IReadOnlyList<ApiEndpoint> Endpoints { get; } = endpoints.Count > 0 ? endpoints : throw new ArgumentException("at least one endpoint", nameof(endpoints));

    public Credentials Credentials { get; } = credentials;

    public RetryPolicy Policy { get; } = policy;

    public async Task<CallResult> SendAsync(ApiRole role, HttpMethod method, string path, string? jsonBody, string? idempotencyKey,
        int firstEndpoint, bool pinned, CancellationToken ct)
    {
        var attempts = new List<AttemptRecord>();
        var key = Credentials.KeyOf(role);
        if (key is null)
        {
            attempts.Add(new AttemptRecord(1, Endpoints[Wrap(firstEndpoint)].Name, null, AttemptClasses.NotSent, "credential-missing", 0,
                $"{Credentials.VariableOf(role)} is not set"));
            return new CallResult(null, null, "credential-missing", attempts);
        }

        int? status = null;
        string? body = null;
        string? code = null;
        for (var attempt = 1; attempt <= Policy.MaxAttempts; attempt++)
        {
            await Task.Delay(Policy.DelayBefore(attempt), ct);
            var endpoint = Endpoints[Wrap(pinned ? firstEndpoint : firstEndpoint + attempt - 1)];
            var watch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Policy.RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl, path));
                request.Headers.Add(OrderRoutes.ApiKeyHeader, key);
                if (idempotencyKey is not null)
                {
                    request.Headers.Add(OrderRoutes.IdempotencyKeyHeader, idempotencyKey);
                }

                if (jsonBody is not null)
                {
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using var response = await endpoint.Http.SendAsync(request, timeout.Token);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(timeout.Token);
                code = ProblemCode(body);
                var ambiguous = AttemptClasses.IsAmbiguous(status.Value);
                attempts.Add(new AttemptRecord(attempt, endpoint.Name, status, ambiguous ? AttemptClasses.Ambiguous : AttemptClasses.Answered, code,
                    watch.ElapsedMilliseconds, null));
                if (!ambiguous)
                {
                    return new CallResult(status, body, code, attempts);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                (status, body, code) = (null, null, "timeout");
                attempts.Add(new AttemptRecord(attempt, endpoint.Name, null, AttemptClasses.Ambiguous, code, watch.ElapsedMilliseconds,
                    $"no answer within {Policy.RequestTimeout.TotalSeconds:0.###} s"));
            }
            catch (HttpRequestException e)
            {
                (status, body, code) = (null, null, "transport");
                attempts.Add(new AttemptRecord(attempt, endpoint.Name, null, AttemptClasses.Ambiguous, code, watch.ElapsedMilliseconds,
                    Credentials.Redactor.Redact(e.Message)));
            }
        }

        return new CallResult(status, body, code, attempts);
    }

    public int Wrap(int index) => ((index % Endpoints.Count) + Endpoints.Count) % Endpoints.Count;

    /// <summary>The "code" extension of a problem response, if any.</summary>
    public static string? ProblemCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
