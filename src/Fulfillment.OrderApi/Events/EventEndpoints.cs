using System.Text.Json;
using Fulfillment.Contracts.Events;
using Fulfillment.Messaging.Outbox;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Events;

/// <summary>
/// Pub/sub subscriptions of the order API (each replica subscribes; the broker gives every message to one of
/// them), the dead-letter recorder, the timeline view, rebuild, and the drill-only publisher.
/// Paths under /dapr and /events answer only the sidecar (app API token).
/// </summary>
public static partial class EventEndpoints
{
    public const string ConsumerAckWithoutApply = "ConsumerAckWithoutApply";
    public const string DeadLetterRoute = "/events/deadletter";

    private static readonly object Success = new { status = "SUCCESS" };
    private static readonly object Retry = new { status = "RETRY" };

    public static void MapEvents(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/dapr/subscribe", () => Results.Ok(
            EventSources.All.Select(s => (object)new
            {
                pubsubname = EventSources.PubSub,
                topic = EventSources.TopicOf(s),
                route = $"/events/{s}",
                deadLetterTopic = EventSources.DeadLetterTopic,
            }).Append(new { pubsubname = EventSources.PubSub, topic = EventSources.DeadLetterTopic, route = DeadLetterRoute })));

        app.MapPost("/events/{source}", async (string source, HttpRequest request, EventConsumer consumer, Failpoints failpoints, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("Fulfillment.OrderApi.Events");
            var (messageId, envelope, error) = await ReadAsync(request, ct);
            if (envelope is null)
            {
                await consumer.RecordAsync(null, messageId, source, null, null, null, Dispositions.Malformed, EventSources.TopicOf(source), ct);
                LogDeadLettered(logger, messageId ?? "-", source, Dispositions.Malformed, error ?? "");
                return Results.Ok(Success);
            }

            var e = IncomingEvent.From(envelope);
            try
            {
                // Drill: acknowledged but not stored, as if the delivery had been lost.
                await failpoints.HitAsync(ConsumerAckWithoutApply, $"{source}:{e.OrderId:D}:{e.Sequence}", ct);
            }
            catch (FailpointSkipException)
            {
                return Results.Ok(Success);
            }

            if (Failpoints.Active && IsPoison(envelope))
            {
                // Drill sentinel: the consumer fails on it every time, so the broker redelivers it a bounded number of
                // times and then moves it to the dead-letter topic.
                LogPoison(logger, e.EventId, source);
                return Results.Json(Retry, statusCode: StatusCodes.Status500InternalServerError);
            }

            try
            {
                var result = await consumer.IngestAsync(source, e, ct);
                LogIngested(logger, e.EventId, source, e.OrderId, e.Sequence, e.Type, result.Disposition, result.LastAppliedSequence, result.Drained);
                return Results.Ok(Success);
            }
            catch (SqlException ex)
            {
                // A transient failure: nothing was committed; the broker redelivers. The error number only: some SQL
                // messages (truncation) quote the values involved.
                LogIngestFailed(logger, e.EventId, source, $"SQL error {ex.Number}");
                return Results.Json(Retry, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost(DeadLetterRoute, async (HttpRequest request, EventConsumer consumer, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var (messageId, envelope, _) = await ReadAsync(request, ct);
            await consumer.RecordAsync(envelope?.EventId, messageId, envelope?.Source, envelope?.OrderId, envelope?.Sequence, envelope?.Type,
                Dispositions.DeliveryFailed, EventSources.DeadLetterTopic, ct);
            LogDeadLettered(loggers.CreateLogger("Fulfillment.OrderApi.Events"), messageId ?? "-", envelope?.Source ?? "-", Dispositions.DeliveryFailed, "");
            return Results.Ok(Success);
        });

        var admin = app.MapGroup("/system").RequireApiKey("ADMIN_API_KEY");
        admin.MapGet("/orders/{orderId:guid}/timeline", async (Guid orderId, TimelineReader reader, CancellationToken ct) =>
            Results.Ok(await reader.ReadAsync(orderId, ct)));
        admin.MapPost("/timeline/rebuild", async (EventConsumer consumer, CancellationToken ct) =>
        {
            var (before, after, streams) = await consumer.RebuildAsync(ct);
            return Results.Ok(new { streams, checksumBefore = before, checksumAfter = after, equal = before == after });
        });
        admin.MapGet("/timeline/checksum", async (string? orders, EventConsumer consumer, CancellationToken ct) =>
        {
            var ids = string.IsNullOrWhiteSpace(orders) ? null : orders.Split(',').Select(Guid.Parse).ToList();
            return Results.Ok(new { checksum = await consumer.ChecksumAsync(ids, ct) });
        });
        admin.MapPost("/outbox/{source}/replay", async (string source, OutboxReplayRequest replay, OutboxReplayClient client, CancellationToken ct) =>
            await client.ReplayAsync(source, replay, ct) is { } result ? Results.Ok(result) : Results.Problem(statusCode: 502, title: "replay failed"));

        if (Failpoints.Active)
        {
            // Drill builds in the drill environment only: publishes a body as given to an event topic (shuffled sets,
            // malformed and poison events).
            admin.MapPost("/drill/publish/{topic}", async (string topic, HttpRequest request, IHttpClientFactory http, CancellationToken ct) =>
            {
                using var body = new StreamReader(request.Body);
                var content = await body.ReadToEndAsync(ct);
                using var client = http.CreateClient("dapr-publish");
                using var message = new StringContent(content, System.Text.Encoding.UTF8, request.ContentType ?? "application/json");
                var id = request.Query["id"].ToString();
                var query = string.IsNullOrEmpty(id) ? "" : $"?metadata.cloudevent.id={Uri.EscapeDataString(id)}";
                using var response = await client.PostAsync($"v1.0/publish/{EventSources.PubSub}/{topic}{query}", message, ct);
                return Results.Ok(new { status = (int)response.StatusCode });
            });
        }
    }

    /// <summary>Reads a CloudEvent from Dapr; its data is the envelope. Returns null (with the reason) when it is not a readable event.</summary>
    private static async Task<(string? MessageId, EventEnvelope? Envelope, string? Error)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        string? messageId = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            messageId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (data.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(data.GetString()!);
                return (messageId, inner.RootElement.Deserialize<EventEnvelope>(DaprEventPublisher.Json), null);
            }

            var envelope = data.Deserialize<EventEnvelope>(DaprEventPublisher.Json);
            return envelope is { Type: not null, Source: not null } ? (messageId, envelope, null) : (messageId, null, "not an event envelope");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentNullException or InvalidOperationException)
        {
            // Only the exception type: a parser's message quotes the input, and payloads never reach the logs.
            return (messageId, null, e.GetType().Name);
        }
    }

    private static bool IsPoison(EventEnvelope envelope) =>
        envelope.Data.ValueKind == JsonValueKind.Object && envelope.Data.TryGetProperty("drillPoison", out var p) && p.ValueKind == JsonValueKind.True;

    [LoggerMessage(Level = LogLevel.Information, Message = "event {EventId} {Source} order={OrderId} seq={Sequence} type={Type}: {Disposition} (applied up to {Last}, drained {Drained})")]
    private static partial void LogIngested(ILogger logger, Guid eventId, string source, Guid orderId, int sequence, string type, string disposition, int last, int drained);

    [LoggerMessage(Level = LogLevel.Warning, Message = "event {EventId} {Source}: ingest failed, redelivery requested: {Error}")]
    private static partial void LogIngestFailed(ILogger logger, Guid eventId, string source, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "drill poison event {EventId} refused ({Source}), redelivery requested")]
    private static partial void LogPoison(ILogger logger, Guid eventId, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "message {MessageId} ({Source}) dead-lettered: {Reason} {Detail}")]
    private static partial void LogDeadLettered(ILogger logger, string messageId, string source, string reason, string detail);
}
