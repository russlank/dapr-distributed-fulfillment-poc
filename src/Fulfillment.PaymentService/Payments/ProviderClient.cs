using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Fulfillment.Contracts.Payments;

namespace Fulfillment.PaymentService.Payments;

/// <summary>Answers of the provider as the payment service classifies them.</summary>
public static class ProviderAnswers
{
    public const string Committed = "Committed";
    public const string Prohibited = "Prohibited";
    public const string NotFound = "NotFound";
    public const string Unknown = "Unknown";

    /// <summary>No connection could be made (refused, name not resolved): the request cannot have reached the provider.</summary>
    public const string Refused = "Refused";

    /// <summary>The provider answered 503 before committing anything.</summary>
    public const string Rejected503 = "Rejected503";

    /// <summary>The request was refused as invalid (4xx other than 409): nothing committed.</summary>
    public const string Invalid = "Invalid";

    public const string Conflict = "Conflict";

    /// <summary>No answer in time after the request was sent: it may have committed.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The connection broke after the request was sent, or another failure: it may have committed.</summary>
    public const string Error = "Error";

    /// <summary>Answers after which a request may have reached the provider without its result being known.</summary>
    public static bool MayHaveReached(string answer) => answer is Timeout or Error;
}

public sealed record ProviderAnswer(string Answer, int? HttpStatus, ProviderOperationResponse? Response, string? Detail);

/// <summary>
/// The provider's HTTP API (ordinary HTTP to an external party; no Dapr). Each call has its own timeout. Failures
/// are classified by whether the request can have reached the provider: a connection that was never made cannot
/// have committed anything; a timeout or a connection broken after sending may have.
/// </summary>
public sealed class ProviderClient(HttpClient http)
{
    private static readonly Meter Meter = new("Fulfillment.Payments");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("provider_requests_total", description: "Requests to the payment provider by call and classified result");

    public Task<ProviderAnswer> SendAsync(ProviderOperationRequest request, TimeSpan timeout, CancellationToken ct) =>
        CallAsync("send", timeout, c => http.PostAsJsonAsync(ProviderRoutes.Operations, request, c), ct);

    public Task<ProviderAnswer> QueryAsync(string operationKey, TimeSpan timeout, CancellationToken ct) =>
        CallAsync("status", timeout, c => http.GetAsync($"{ProviderRoutes.Operations}/{Uri.EscapeDataString(operationKey)}", c), ct);

    private static async Task<ProviderAnswer> CallAsync(string call, TimeSpan timeout, Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(timeout);
        ProviderAnswer answer;
        try
        {
            using var response = await send(attempt.Token);
            answer = await ClassifyAsync(response, attempt.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested && !attempt.IsCancellationRequested && e.InnerException is TimeoutException)
        {
            // The handler's connect timeout (shorter than the request timeout): no connection, nothing sent.
            answer = new ProviderAnswer(ProviderAnswers.Refused, null, null, "connect timeout: " + e.InnerException.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answer = new ProviderAnswer(ProviderAnswers.Timeout, null, null, $"no answer within {timeout.TotalSeconds:0.###} s");
        }
        catch (HttpRequestException e) when (NeverConnected(e))
        {
            answer = new ProviderAnswer(ProviderAnswers.Refused, null, null, Describe(e));
        }
        catch (HttpRequestException e)
        {
            answer = new ProviderAnswer(ProviderAnswers.Error, null, null, Describe(e));
        }

        Requests.Add(1, new KeyValuePair<string, object?>("call", call), new KeyValuePair<string, object?>("result", answer.Answer.ToLowerInvariant()));
        return answer;
    }

    private static async Task<ProviderAnswer> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadFromJsonAsync<ProviderOperationResponse>(ct)
                ?? throw new HttpRequestException("empty provider answer");
            return new ProviderAnswer(body.Status, status, body, null);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Conflict => new ProviderAnswer(ProviderAnswers.Conflict, status, null, "operation key reused with another payload"),
            HttpStatusCode.ServiceUnavailable => new ProviderAnswer(ProviderAnswers.Rejected503, status, null, "rejected before commit"),
            >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => new ProviderAnswer(ProviderAnswers.Invalid, status, null, response.ReasonPhrase),
            _ => new ProviderAnswer(ProviderAnswers.Error, status, null, response.ReasonPhrase),
        };
    }

    /// <summary>
    /// True when no connection was established (name not resolved, connection refused or unreachable, or the
    /// connect timeout elapsed), so no request bytes can have reached the provider. A failure on an established
    /// connection is never classified here.
    /// </summary>
    private static bool NeverConnected(HttpRequestException e) =>
        e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError;

    private static string Describe(HttpRequestException e) =>
        $"{e.HttpRequestError}: {(e.InnerException is SocketException s ? s.SocketErrorCode.ToString() : e.Message)}";
}
