using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fulfillment.Contracts.Events;

namespace Fulfillment.OrderApi.Events;

/// <summary>An event as the consumer received it, with the hash that identifies its content.</summary>
public sealed record IncomingEvent(Guid EventId, string Source, Guid OrderId, int Sequence, string Type, int SchemaVersion, DateTime OccurredAtUtc, string Data,
    string PayloadHash)
{
    /// <summary>
    /// The content hash: source, order, sequence, type, schema version, and the data as published. A republished
    /// event (same stored row) hashes the same; any change of content does not.
    /// </summary>
    public static string HashOf(string source, Guid orderId, int sequence, string type, int schemaVersion, string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', source, orderId.ToString("D"), sequence.ToString(CultureInfo.InvariantCulture), type,
                schemaVersion.ToString(CultureInfo.InvariantCulture), data))));

    public static IncomingEvent From(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var data = envelope.Data.ValueKind == JsonValueKind.Undefined ? "null" : JsonSerializer.Serialize(envelope.Data);
        return new IncomingEvent(envelope.EventId, envelope.Source, envelope.OrderId, envelope.Sequence, envelope.Type, envelope.SchemaVersion,
            envelope.OccurredAtUtc, data, HashOf(envelope.Source, envelope.OrderId, envelope.Sequence, envelope.Type, envelope.SchemaVersion, data));
    }
}

/// <summary>What happened to an incoming event (history disposition, or the dead-letter reason).</summary>
public static class Dispositions
{
    public const string Applied = "Applied";
    public const string Pending = "Pending";
    public const string Duplicate = "Duplicate";
    public const string EventIdPayloadConflict = "EventIdPayloadConflict";
    public const string SequenceConflict = "SequenceConflict";
    public const string SourceMismatch = "SourceMismatch";
    public const string UnknownSchemaVersion = "UnknownSchemaVersion";
    public const string InvalidPayload = "InvalidPayload";
    public const string Malformed = "Malformed";
    public const string DeliveryFailed = "DeliveryFailed";

    public static bool IsDeadLetter(string disposition) => disposition is not (Applied or Pending or Duplicate);
}

/// <summary>
/// The pure rules of the event consumer (plan 7.8), shared by the SQL consumer and the in-memory model the
/// permutation tests use: classification of an incoming event against history and current state, and
/// application of one event to its source's fields. Current state advances one sequence at a time.
/// </summary>
public static class TimelineProjection
{
    /// <summary>
    /// Classifies an event of stream <paramref name="streamSource"/>. <paramref name="storedHashOfEventId"/> is the hash
    /// stored for the same event id (null if new); <paramref name="eventIdAtSequence"/> is the event id already stored at
    /// the same (source, order, sequence) (null if none).
    /// </summary>
    public static string Classify(IncomingEvent e, string streamSource, string? storedHashOfEventId, Guid? eventIdAtSequence, int lastAppliedSequence)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.SchemaVersion != EventTypes.CurrentSchemaVersion)
        {
            return Dispositions.UnknownSchemaVersion;
        }

        if (e.Source != streamSource || EventTypes.SourceOf(e.Type) != streamSource)
        {
            return Dispositions.SourceMismatch;
        }

        if (e.Sequence < 1 || e.OrderId == Guid.Empty || e.EventId == Guid.Empty || !IsValidData(e.Type, e.Data))
        {
            return Dispositions.InvalidPayload;
        }

        if (storedHashOfEventId is not null)
        {
            return storedHashOfEventId == e.PayloadHash ? Dispositions.Duplicate : Dispositions.EventIdPayloadConflict;
        }

        if (eventIdAtSequence is not null || e.Sequence <= lastAppliedSequence)
        {
            return Dispositions.SequenceConflict;
        }

        return e.Sequence == lastAppliedSequence + 1 ? Dispositions.Applied : Dispositions.Pending;
    }

    /// <summary>True when the data carries what applying the event needs.</summary>
    public static bool IsValidData(string type, string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return type switch
            {
                EventTypes.OrderStatusChanged => Has(root, "toStatus"),
                EventTypes.OrderCompleted => true,
                EventTypes.HoldCreated or EventTypes.HoldReleased or EventTypes.HoldExpired or EventTypes.HoldConsumed or EventTypes.HoldExtended => Has(root, "holdId"),
                EventTypes.PaymentOperationRecorded => Has(root, "operationKey") && Has(root, "kind") && Has(root, "outcome"),
                EventTypes.ShipmentDispatched => true,
                EventTypes.CarrierStatusRecorded => Has(root, "status") && Has(root, "version") && Has(root, "outcome"),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Applies one event to its source's fields (a new dictionary; the input is not changed).</summary>
    public static SortedDictionary<string, string> Apply(IReadOnlyDictionary<string, string> fields, string type, string data)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var next = new SortedDictionary<string, string>(fields.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        switch (type)
        {
            case EventTypes.OrderStatusChanged:
                next["status"] = Text(root, "toStatus");
                next["reason"] = Text(root, "reasonCode");
                break;
            case EventTypes.OrderCompleted:
                next["status"] = Has(root, "toStatus") ? Text(root, "toStatus") : "Completed";
                next["reason"] = Text(root, "reasonCode");
                next["completed"] = "true";
                break;
            case EventTypes.HoldCreated:
                next[$"hold:{Text(root, "holdId")}"] = "Held";
                break;
            case EventTypes.HoldReleased:
                next[$"hold:{Text(root, "holdId")}"] = "Released";
                break;
            case EventTypes.HoldExpired:
                next[$"hold:{Text(root, "holdId")}"] = "Expired";
                break;
            case EventTypes.HoldConsumed:
                next[$"hold:{Text(root, "holdId")}"] = "Consumed";
                break;
            case EventTypes.HoldExtended:
                next[$"holdExpiresAt:{Text(root, "holdId")}"] = Text(root, "expiresAt");
                break;
            case EventTypes.PaymentOperationRecorded:
                next[$"op:{Text(root, "operationKey")}"] = Text(root, "outcome");
                next["last"] = $"{Text(root, "kind")}:{Text(root, "outcome")}";
                break;
            case EventTypes.ShipmentDispatched:
                next["shipment"] = "Dispatched";
                break;
            case EventTypes.CarrierStatusRecorded:
                var outcome = Text(root, "outcome");
                if (outcome is "Applied" or "TerminalApplied")
                {
                    next["carrierStatus"] = Text(root, "status");
                    next["carrierVersion"] = Text(root, "version");
                }
                else if (outcome == "TerminalConflict")
                {
                    next["terminalConflicts"] = (int.Parse(next.GetValueOrDefault("terminalConflicts", "0"), CultureInfo.InvariantCulture) + 1)
                        .ToString(CultureInfo.InvariantCulture);
                }

                break;
            default:
                throw new InvalidOperationException($"no rule for event type {type}");
        }

        return next;
    }

    public static string Serialize(IReadOnlyDictionary<string, string> fields) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(fields.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal));

    public static SortedDictionary<string, string> Deserialize(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.Ordinal);

    /// <summary>
    /// The overall stage shown by the timeline, computed from the per-source rows when read and never stored:
    /// the order's status from the orders source wins once it is terminal; otherwise the carrier status while a
    /// shipment exists; otherwise the order's status.
    /// </summary>
    public static string CombinedStage(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bySource)
    {
        ArgumentNullException.ThrowIfNull(bySource);
        var status = bySource.TryGetValue(EventSources.Orders, out var orders) ? orders.GetValueOrDefault("status") : null;
        if (status is "Completed" or "Rejected" or "Compensated" or "Cancelled")
        {
            return status;
        }

        if (bySource.TryGetValue(EventSources.Shipping, out var shipping) && shipping.GetValueOrDefault("carrierStatus") is { } carrier)
        {
            return $"Shipping:{carrier}";
        }

        return status ?? "Unknown";
    }

    /// <summary>Checksum over current-state rows (order, source, last applied sequence, fields), in key order.</summary>
    public static string Checksum(IEnumerable<(Guid OrderId, string Source, int LastApplied, string Fields)> rows)
    {
        var text = string.Join('\n', rows.OrderBy(r => r.OrderId).ThenBy(r => r.Source, StringComparer.Ordinal)
            .Select(r => $"{r.OrderId:D}|{r.Source}|{r.LastApplied}|{r.Fields}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool Has(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Null ? "" : value.GetRawText()
            : "";
}

/// <summary>
/// In-memory model of one consumer (history plus current state, one stream per source and order) built only
/// from the pure rules; the SQL consumer implements the same steps in one transaction. Used by the
/// deterministic permutation tests and as the reference for rebuilds.
/// </summary>
public sealed class TimelineModel
{
    private readonly Dictionary<Guid, (IncomingEvent Event, string Disposition)> history = [];
    private readonly Dictionary<(string Source, Guid OrderId), (int LastApplied, SortedDictionary<string, string> Fields)> state = [];

    public List<(Guid EventId, string Reason)> DeadLetters { get; } = [];

    public Dictionary<Guid, int> Duplicates { get; } = [];

    public string Ingest(string streamSource, IncomingEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var key = (streamSource, e.OrderId);
        var (lastApplied, fields) = state.TryGetValue(key, out var current) ? current : (0, new SortedDictionary<string, string>(StringComparer.Ordinal));
        var stored = history.TryGetValue(e.EventId, out var h) ? h.Event.PayloadHash : null;
        var atSequence = history.Values.Where(x => x.Event.Source == streamSource && x.Event.OrderId == e.OrderId && x.Event.Sequence == e.Sequence)
            .Select(x => (Guid?)x.Event.EventId).FirstOrDefault();
        var disposition = TimelineProjection.Classify(e, streamSource, stored, atSequence, lastApplied);
        switch (disposition)
        {
            case Dispositions.Duplicate:
                Duplicates[e.EventId] = Duplicates.GetValueOrDefault(e.EventId) + 1;
                return disposition;
            case Dispositions.Pending:
                history[e.EventId] = (e, Dispositions.Pending);
                return disposition;
            case Dispositions.Applied:
                history[e.EventId] = (e, Dispositions.Applied);
                fields = TimelineProjection.Apply(fields, e.Type, e.Data);
                lastApplied = e.Sequence;
                while (history.Values.FirstOrDefault(x => x.Event.Source == streamSource && x.Event.OrderId == e.OrderId && x.Event.Sequence == lastApplied + 1
                                                          && x.Disposition == Dispositions.Pending) is { Event: not null } next)
                {
                    history[next.Event.EventId] = (next.Event, Dispositions.Applied);
                    fields = TimelineProjection.Apply(fields, next.Event.Type, next.Event.Data);
                    lastApplied++;
                }

                state[key] = (lastApplied, fields);
                return disposition;
            default:
                DeadLetters.Add((e.EventId, disposition));
                return disposition;
        }
    }

    public IReadOnlyDictionary<Guid, string> History => history.ToDictionary(kv => kv.Key, kv => kv.Value.Disposition);

    public string Checksum() => TimelineProjection.Checksum(state.Select(kv => (kv.Key.OrderId, kv.Key.Source, kv.Value.LastApplied,
        TimelineProjection.Serialize(kv.Value.Fields))));

    public int LastApplied(string source, Guid orderId) => state.TryGetValue((source, orderId), out var s) ? s.LastApplied : 0;

    public IReadOnlyDictionary<string, string> Fields(string source, Guid orderId) =>
        state.TryGetValue((source, orderId), out var s) ? s.Fields : new SortedDictionary<string, string>(StringComparer.Ordinal);
}
