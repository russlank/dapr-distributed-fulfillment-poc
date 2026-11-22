using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Fulfillment.Contracts.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fulfillment.Messaging.Outbox;

public sealed class OutboxRelayOptions
{
    public const string Section = "OutboxRelay";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public int BatchSize { get; set; } = 20;

    /// <summary>How long a claimed row is reserved for this relay; longer than one publication takes.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan FirstBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Attempts after which a row is flagged <c>Stalled</c> (it is still retried).</summary>
    public int StallThreshold { get; set; } = 10;

    /// <summary>Per publication; a publication that times out may still have reached the broker (it is retried).</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A pending row older than this counts as aging (metric <c>outbox_aging</c>, alert OutboxAging).</summary>
    public TimeSpan AgingThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the outbox depth is read for the metrics.</summary>
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(15);

    public static void ForEnvironment(OutboxRelayOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.Lease = TimeSpan.FromSeconds(10);
            options.MaxBackoff = TimeSpan.FromSeconds(10);
            options.StallThreshold = 5;
            options.AgingThreshold = TimeSpan.FromSeconds(15);
            options.HealthInterval = TimeSpan.FromSeconds(5);
        }
    }
}

public sealed record PublishOutcome(bool Acknowledged, string? Error);

/// <summary>Publishes through the Dapr sidecar (pub/sub component <c>pubsub</c>); a 2xx answer is the broker's acknowledgement.</summary>
public sealed class DaprEventPublisher(HttpClient http, OutboxRelayOptions options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PublishOutcome> PublishAsync(string topic, EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.PublishTimeout);
        try
        {
            // The CloudEvent id is the event id, so broker-side tools and taps can count deliveries per event.
            using var response = await http.PostAsJsonAsync(
                $"v1.0/publish/{EventSources.PubSub}/{topic}?metadata.cloudevent.id={envelope.EventId}", envelope, Json, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return new PublishOutcome(true, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return new PublishOutcome(false, $"{(int)response.StatusCode} {body[..Math.Min(body.Length, 150)]}");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Unknown whether the broker got it: the row stays Pending and is published again (a duplicate at worst).
            return new PublishOutcome(false, e is OperationCanceledException ? "timeout" : e.Message);
        }
    }
}

/// <summary>
/// Publishes a source's outbox at least once. Each relay instance (one per replica) leases due rows, publishes
/// them in sequence order per order, and marks each published after the broker's acknowledgement. A crash
/// between the acknowledgement and the mark, an expired lease, or a replay publishes a row again; consumers
/// apply events once per event id. Failures leave the row <c>Pending</c> with backoff, flagged <c>Stalled</c>
/// after a threshold and still retried.
/// </summary>
public sealed partial class OutboxRelay(OutboxStore store, DaprEventPublisher publisher, OutboxRelayOptions options, string owner,
    Func<string, string, CancellationToken, Task> checkpoint, ILogger<OutboxRelay> logger) : BackgroundService
{
    public const string AfterBrokerAckBeforeOutboxMarkSent = "AfterBrokerAckBeforeOutboxMarkSent";

    private static readonly Meter Meter = new("Fulfillment.Outbox");
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("outbox_published_total");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("outbox_publish_failures_total");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OutboxHealth> Health = new();

    static OutboxRelay()
    {
        Meter.CreateObservableGauge("outbox_pending", () => Measure(h => h.Pending), description: "pending outbox rows per source");
        Meter.CreateObservableGauge("outbox_aging", () => Measure(h => h.Aging), description: "pending outbox rows older than the aging threshold per source");
        Meter.CreateObservableGauge("outbox_stalled", () => Measure(h => h.Stalled), description: "stalled outbox rows per source (still retried)");
    }

    private static IEnumerable<Measurement<int>> Measure(Func<OutboxHealth, int> value) =>
        Health.Select(h => new Measurement<int>(value(h.Value), new KeyValuePair<string, object?>("source", h.Key)));

    private DateTime nextHealth = DateTime.MinValue;

    /// <summary>Reads the outbox depth for the metrics when due; a failure only leaves the last values.</summary>
    private async Task RefreshHealthAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < nextHealth)
        {
            return;
        }

        nextHealth = DateTime.UtcNow + options.HealthInterval;
        try
        {
            Health[store.Table.Source] = await store.HealthAsync(options.AgingThreshold, ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // Reported by the cycle's own failure handling when SQL is down.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await RefreshHealthAsync(stoppingToken);
                worked = await RunOnceAsync(stoppingToken) > 0;
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogCycleFailed(logger, store.Table.Source, e.Message);
            }

            if (!worked)
            {
                try
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One claim-and-publish cycle; returns the number of rows claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var rows = await store.ClaimAsync(owner, options.BatchSize, options.Lease, ct);
        // Rows go out in sequence order per order, but a failed row does not hold back later ones: the consumer
        // applies each order's events contiguously and keeps an early arrival pending until the gap is filled.
        foreach (var row in rows)
        {
            var envelope = new EventEnvelope(row.EventId, store.Table.Source, row.OrderId, row.Sequence, row.EventType, row.SchemaVersion, row.CreatedAtUtc,
                JsonDocument.Parse(row.Payload).RootElement.Clone());
            var outcome = await publisher.PublishAsync(store.Table.Topic, envelope, ct);
            if (!outcome.Acknowledged)
            {
                Failed.Add(1, new KeyValuePair<string, object?>("source", store.Table.Source));
                LogPublishFailed(logger, store.Table.Source, row.EventId, row.OrderId, row.Sequence, row.Attempts + 1, outcome.Error ?? "");
                await store.RecordFailureAsync(row.Id, row.Generation, owner, outcome.Error ?? "unknown", options.FirstBackoff, options.MaxBackoff,
                    options.StallThreshold, ct);
                continue;
            }

            await checkpoint(AfterBrokerAckBeforeOutboxMarkSent, row.EventId.ToString(), ct);
            var marked = await store.MarkPublishedAsync(row.Id, row.Generation, ct);
            Published.Add(1, new KeyValuePair<string, object?>("source", store.Table.Source));
            LogPublished(logger, store.Table.Source, row.EventId, row.OrderId, row.Sequence, row.EventType, marked);
        }

        return rows.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "outbox {Source}: published {EventId} order={OrderId} seq={Sequence} type={Type} marked={Marked}")]
    private static partial void LogPublished(ILogger logger, string source, Guid eventId, Guid orderId, int sequence, string type, bool marked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "outbox {Source}: publication of {EventId} order={OrderId} seq={Sequence} failed (attempt {Attempt}): {Error}")]
    private static partial void LogPublishFailed(ILogger logger, string source, Guid eventId, Guid orderId, int sequence, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "outbox {Source}: relay cycle failed: {Error}")]
    private static partial void LogCycleFailed(ILogger logger, string source, string error);
}
