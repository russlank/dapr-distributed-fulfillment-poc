using System.Diagnostics.Metrics;
using System.Text.Json;
using Fulfillment.Contracts.Shipping;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Carrier;

/// <summary>
/// The carrier's callback ingress (the order API is the single external entry point): the HMAC signature and
/// timestamp are checked (401), the contract validated (400), then one OrderDb transaction writes the inbox row and
/// the forward command, and the answer is 202 ("durably accepted", not "processed"). A repeated event id with the
/// same payload is a counted duplicate (202); with another payload it is refused (409) and audited. The ingress does
/// not depend on Shipping or the workflow being available.
/// </summary>
public static partial class CarrierIngress
{
    private static readonly Meter Meter = new("Fulfillment.OrderApi.Carrier");
    private static readonly Counter<long> Callbacks = Meter.CreateCounter<long>("carrier_callbacks_total", description: "carrier callbacks by ingress result");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapCarrierIngress(this WebApplication app, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(CarrierCallbackHeaders.Route, async (HttpContext http, ILoggerFactory loggers, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var key = Environment.GetEnvironmentVariable(CarrierCallbackHeaders.HmacKeyVariable) ?? "";
            if (!CarrierSignature.Verify(key, http.Request.Headers[CarrierCallbackHeaders.Timestamp], http.Request.Headers[CarrierCallbackHeaders.Signature], body, DateTimeOffset.UtcNow))
            {
                Callbacks.Add(1, new KeyValuePair<string, object?>("result", "unauthorized"));
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "invalid carrier signature");
            }

            CarrierCallback? callback;
            try
            {
                callback = JsonSerializer.Deserialize<CarrierCallback>(body, Json);
            }
            catch (JsonException)
            {
                callback = null;
            }

            if (callback is null || callback.EventId == Guid.Empty || callback.ShipmentId == Guid.Empty || callback.OrderId == Guid.Empty
                || !CarrierStatuses.IsKnown(callback.Status) || callback.Version is < 0 or > 10_000)
            {
                Callbacks.Add(1, new KeyValuePair<string, object?>("result", "invalid"));
                return Results.BadRequest(new { error = "invalid-callback" });
            }

            var result = await AcceptAsync(connectionString, callback, ct);
            Callbacks.Add(1, new KeyValuePair<string, object?>("result", result));
            var logger = loggers.CreateLogger("Fulfillment.OrderApi.Carrier");
            Log.Accepted(logger, callback.EventId, callback.ShipmentId, callback.Status, callback.Version, result);
            return result == "conflict"
                ? Results.Conflict(new { error = "event-id-payload-conflict", eventId = callback.EventId })
                : Results.Accepted(value: new { eventId = callback.EventId, duplicate = result == "duplicate" });
        });
    }

    /// <summary>The ingress transaction; returns accepted, duplicate, or conflict.</summary>
    public static async Task<string> AcceptAsync(string connectionString, CarrierCallback c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        var hash = CarrierEventHash.Of(c.EventId, c.ShipmentId, c.OrderId, c.Status, c.Version, c.OccurredAtUtc);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(ct);
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
                string? stored = null;
                await using (var read = new SqlCommand("SELECT PayloadHash FROM orders.CarrierCallbackInbox WITH (UPDLOCK, HOLDLOCK) WHERE Source = 'carrier' AND EventId = @e", connection, tx))
                {
                    read.Parameters.AddWithValue("@e", c.EventId);
                    stored = (string?)await read.ExecuteScalarAsync(ct);
                }

                string result;
                if (stored is null)
                {
                    await using var insert = new SqlCommand(
                        """
                        INSERT orders.CarrierCallbackInbox (Source, EventId, PayloadHash, ShipmentId, OrderId, Status, Version, OccurredAt, Duplicates)
                        VALUES ('carrier', @e, @h, @s, @o, @st, @v, @at, 0);
                        INSERT orders.CarrierStatusForward (EventId, State, Attempts) VALUES (@e, 'Pending', 0);
                        """, connection, tx);
                    insert.Parameters.AddWithValue("@e", c.EventId);
                    insert.Parameters.AddWithValue("@h", hash);
                    insert.Parameters.AddWithValue("@s", c.ShipmentId);
                    insert.Parameters.AddWithValue("@o", c.OrderId);
                    insert.Parameters.AddWithValue("@st", c.Status);
                    insert.Parameters.AddWithValue("@v", c.Version);
                    insert.Parameters.AddWithValue("@at", c.OccurredAtUtc);
                    await insert.ExecuteNonQueryAsync(ct);
                    result = "accepted";
                }
                else if (stored == hash)
                {
                    await using var dup = new SqlCommand("UPDATE orders.CarrierCallbackInbox SET Duplicates = Duplicates + 1 WHERE Source = 'carrier' AND EventId = @e", connection, tx);
                    dup.Parameters.AddWithValue("@e", c.EventId);
                    await dup.ExecuteNonQueryAsync(ct);
                    result = "duplicate";
                }
                else
                {
                    await using var audit = new SqlCommand(
                        "INSERT orders.CarrierCallbackConflict (EventId, StoredHash, ReceivedHash, DetectedBy) VALUES (@e, @s, @r, 'ingress')", connection, tx);
                    audit.Parameters.AddWithValue("@e", c.EventId);
                    audit.Parameters.AddWithValue("@s", stored);
                    audit.Parameters.AddWithValue("@r", hash);
                    await audit.ExecuteNonQueryAsync(ct);
                    result = "conflict";
                }

                await tx.CommitAsync(ct);
                return result;
            }
            catch (SqlException e) when (e.Number is 1205 or 2627 or 2601 && attempt < 5)
            {
                // A concurrent copy of the same event committed first: read it again.
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "carrier callback {EventId} for {ShipmentId} ({Status} v{Version}): {Result}")]
        public static partial void Accepted(ILogger logger, Guid eventId, Guid shipmentId, string status, int version, string result);
    }
}
