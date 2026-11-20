using System.Net;
using System.Net.Http.Json;
using Fulfillment.Contracts.Shipping;
using Microsoft.Data.SqlClient;

namespace Fulfillment.ShippingService.Shipping;

public sealed class ShippingOptions
{
    public const string Section = "Shipping";

    public Uri CarrierUrl { get; set; } = new("http://carrier-simulator:8080");

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxSends { get; set; } = 4;

    public TimeSpan SendBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A terminal status is expected this long after the carrier accepted the shipment.</summary>
    public TimeSpan DeliveryDeadline { get; set; } = TimeSpan.FromMinutes(30);

    public static void ForEnvironment(ShippingOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.RequestTimeout = TimeSpan.FromSeconds(2);
            options.SendBackoff = TimeSpan.FromMilliseconds(500);
            options.DeliveryDeadline = TimeSpan.FromSeconds(45);
        }
    }
}

/// <summary>
/// Shipping's operations: the shipment (one per order; its id is the carrier's idempotency key), dispatch to the
/// carrier, and the ingest of carrier status events with the current-state rule. Every valid unique event is kept
/// with its outcome; the current status moves only forward by version, and a terminal status, once set, never changes.
/// </summary>
public sealed partial class ShippingOperations(string connectionString, HttpClient carrier, ShippingOptions options, string replica, ILogger<ShippingOperations> logger)
{
    /// <summary>The shipment id of an order: deterministic, so every retry and run asks the carrier with the same key.</summary>
    public static Guid ShipmentIdOf(Guid orderId) => orderId;

    public async Task<DispatchShipmentResult> DispatchAsync(DispatchShipmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipmentId = ShipmentIdOf(request.OrderId);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await Exec(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM shipping.Shipment WITH (UPDLOCK, HOLDLOCK) WHERE ShipmentId = @s)
                INSERT shipping.Shipment (ShipmentId, OrderId, Parcels, State, CurrentVersion) VALUES (@s, @o, @p, 'Prepared', 0);
            """, ct, ("@s", shipmentId), ("@o", request.OrderId), ("@p", request.Parcels));
        if (await DispatchedAsync(connection, shipmentId, ct) is { } done)
        {
            return done;
        }

        var sends = 0;
        var maySent = false;
        while (sends < options.MaxSends)
        {
            if (sends > 0)
            {
                await Task.Delay(options.SendBackoff * Math.Pow(2, sends - 1), ct);
            }

            sends++;
            var attempt = await ScalarAsync<long>(connection, "INSERT shipping.DispatchAttempt (ShipmentId, Replica) OUTPUT inserted.Id VALUES (@s, @r)", ct, ("@s", shipmentId), ("@r", replica));
            var (answer, status, detail, accepted) = await SendAsync(new CarrierShipmentRequest(shipmentId, request.OrderId, request.Parcels), ct);
            await Exec(connection, "UPDATE shipping.DispatchAttempt SET Answer = @a, HttpStatus = @h, Detail = @d WHERE Id = @id", ct,
                ("@a", answer), ("@h", (object?)status ?? DBNull.Value), ("@d", (object?)detail ?? DBNull.Value), ("@id", attempt));
            LogDispatch(logger, shipmentId, sends, answer, detail);
            if (accepted is not null)
            {
                await Exec(connection,
                    """
                    UPDATE shipping.Shipment SET State = 'Dispatched', CarrierAcceptedAt = SYSUTCDATETIME(),
                        DeliveryDeadlineAt = DATEADD(millisecond, @d, SYSUTCDATETIME())
                    WHERE ShipmentId = @s AND State <> 'Dispatched';
                    """, ct, ("@s", shipmentId), ("@d", (int)options.DeliveryDeadline.TotalMilliseconds));
                return (await DispatchedAsync(connection, shipmentId, ct))!;
            }

            // A timeout or broken connection may have reached the carrier: the same shipment id is sent again (the
            // carrier answers a repeat with its stored acceptance), never a new one.
            maySent |= answer is "Timeout" or "Error";
        }

        return new DispatchShipmentResult(maySent ? DispatchResults.Ambiguous : DispatchResults.Unavailable, shipmentId, null, sends, replica);
    }

    private async Task<(string Answer, int? Status, string? Detail, CarrierShipmentAccepted? Accepted)> SendAsync(CarrierShipmentRequest request, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(options.RequestTimeout);
        try
        {
            using var response = await carrier.PostAsJsonAsync(CarrierRoutes.Shipments, request, attempt.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return ("Accepted", 200, null, await response.Content.ReadFromJsonAsync<CarrierShipmentAccepted>(attempt.Token));
            }

            return ((int)response.StatusCode >= 500 ? "Error" : "Refused" + (int)response.StatusCode, (int)response.StatusCode, response.ReasonPhrase, null);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested && !attempt.IsCancellationRequested && e.InnerException is TimeoutException)
        {
            return ("Refused", null, "connect timeout", null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("Timeout", null, $"no answer within {options.RequestTimeout.TotalSeconds:0.###} s", null);
        }
        catch (HttpRequestException e) when (e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        {
            return ("Refused", null, e.HttpRequestError.ToString(), null);
        }
        catch (HttpRequestException e)
        {
            return ("Error", null, e.HttpRequestError + ": " + e.Message, null);
        }
    }

    private async Task<DispatchShipmentResult?> DispatchedAsync(SqlConnection connection, Guid shipmentId, CancellationToken ct)
    {
        await using var read = new SqlCommand(
            """
            SELECT DeliveryDeadlineAt, (SELECT COUNT(*) FROM shipping.DispatchAttempt a WHERE a.ShipmentId = s.ShipmentId)
            FROM shipping.Shipment s WHERE ShipmentId = @s AND State = 'Dispatched';
            """, connection);
        read.Parameters.AddWithValue("@s", shipmentId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new DispatchShipmentResult(DispatchResults.Dispatched, shipmentId, DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetInt32(1), replica)
            : null;
    }

    public async Task<ShipmentStatusResult> StatusAsync(Guid orderId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var read = new SqlCommand(
            """
            SELECT ShipmentId, CurrentStatus, TerminalStatus, DeliveryDeadlineAt,
                   CASE WHEN DeliveryDeadlineAt IS NOT NULL AND DeliveryDeadlineAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END
            FROM shipping.Shipment WHERE OrderId = @o;
            """, connection);
        read.Parameters.AddWithValue("@o", orderId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"no shipment for order {orderId}");
        }

        return new ShipmentStatusResult(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc), reader.GetInt32(4) == 1);
    }

    /// <summary>Asks the carrier for the latest transition and ingests it like a callback (same event id, so a later callback is a duplicate).</summary>
    public async Task<ShipmentStatusResult> PollAsync(Guid orderId, CancellationToken ct)
    {
        var shipmentId = ShipmentIdOf(orderId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            using var response = await carrier.GetAsync($"{CarrierRoutes.Shipments}/{shipmentId}", timeout.Token);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<CarrierShipmentStatus>(timeout.Token) is { EventId: var id } latest && id != Guid.Empty)
            {
                await IngestAsync(new CarrierStatusIngest(latest.EventId, shipmentId, orderId, latest.Status, latest.Version, latest.OccurredAtUtc, "poll",
                    CarrierEventHash.Of(latest.EventId, shipmentId, orderId, latest.Status, latest.Version, latest.OccurredAtUtc)), ct);
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogPollFailed(logger, shipmentId, e.Message);
        }

        return await StatusAsync(orderId, ct);
    }

    /// <summary>
    /// Ingests one carrier status event in one transaction (the shipment row locked): a repeated event id returns the
    /// stored outcome (another payload: PayloadConflict); an unknown shipment, a wrong order, or an already recorded
    /// version is answered without changing the shipment; otherwise the event is stored with the outcome the
    /// current-state rule gives it.
    /// </summary>
    public async Task<CarrierStatusIngested> IngestAsync(CarrierStatusIngest e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        string? orderOfShipment = null, terminal = null;
        var currentVersion = 0;
        await using (var read = new SqlCommand("SELECT OrderId, TerminalStatus, CurrentVersion FROM shipping.Shipment WITH (UPDLOCK, ROWLOCK) WHERE ShipmentId = @s", connection, tx))
        {
            read.Parameters.AddWithValue("@s", e.ShipmentId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                orderOfShipment = reader.GetGuid(0).ToString();
                terminal = reader.IsDBNull(1) ? null : reader.GetString(1);
                currentVersion = reader.GetInt32(2);
            }
        }

        await using (var stored = new SqlCommand("SELECT PayloadHash, Outcome FROM shipping.CarrierStatusEvent WHERE EventId = @e", connection, tx))
        {
            stored.Parameters.AddWithValue("@e", e.EventId);
            await using var reader = await stored.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var (hash, outcome) = (reader.GetString(0), reader.GetString(1));
                await reader.DisposeAsync();
                if (hash != e.PayloadHash)
                {
                    await tx.CommitAsync(ct);
                    return new CarrierStatusIngested(e.EventId, CarrierStatusOutcomes.PayloadConflict, false, terminal);
                }

                await Exec(connection, tx, "UPDATE shipping.CarrierStatusEvent SET Replays = Replays + 1 WHERE EventId = @e", ct, ("@e", e.EventId));
                await tx.CommitAsync(ct);
                return new CarrierStatusIngested(e.EventId, outcome, true, terminal);
            }
        }

        if (orderOfShipment is null)
        {
            await tx.CommitAsync(ct);
            return new CarrierStatusIngested(e.EventId, CarrierStatusOutcomes.UnknownShipment, false, null);
        }

        // A callback naming another order than the shipment's is always recorded as a mismatch, whatever its
        // version: it must never be taken for a duplicate of the shipment's own event at that version.
        var mismatch = Guid.Parse(orderOfShipment) != e.OrderId;
        var versionTaken = !mismatch && await ScalarAsync<int>(connection, tx,
            "SELECT COUNT(*) FROM shipping.CarrierStatusEvent WHERE ShipmentId = @s AND Version = @v AND Outcome <> 'CorrelationMismatch'", ct,
            ("@s", e.ShipmentId), ("@v", e.Version)) > 0;
        if (versionTaken)
        {
            await tx.CommitAsync(ct);
            return new CarrierStatusIngested(e.EventId, CarrierStatusOutcomes.Duplicate, false, terminal);
        }

        string result;
        if (mismatch)
        {
            result = CarrierStatusOutcomes.CorrelationMismatch;
        }
        else if (terminal is not null)
        {
            result = !CarrierStatuses.IsTerminal(e.Status) ? CarrierStatusOutcomes.Stale
                : e.Status == terminal ? CarrierStatusOutcomes.TerminalRepeat
                : CarrierStatusOutcomes.TerminalConflict;
        }
        else if (e.Version > currentVersion)
        {
            result = CarrierStatuses.IsTerminal(e.Status) ? CarrierStatusOutcomes.TerminalApplied : CarrierStatusOutcomes.Applied;
        }
        else
        {
            result = CarrierStatusOutcomes.Stale;
        }

        await Exec(connection, tx,
            """
            INSERT shipping.CarrierStatusEvent (EventId, ShipmentId, OrderId, Status, Version, OccurredAt, Source, PayloadHash, Outcome, Replays)
            VALUES (@e, @s, @o, @st, @v, @at, @src, @h, @out, 0);
            """, ct, ("@e", e.EventId), ("@s", e.ShipmentId), ("@o", e.OrderId), ("@st", e.Status), ("@v", e.Version), ("@at", e.OccurredAtUtc), ("@src", e.Source),
            ("@h", e.PayloadHash), ("@out", result));
        if (result is CarrierStatusOutcomes.Applied or CarrierStatusOutcomes.TerminalApplied)
        {
            await Exec(connection, tx,
                """
                UPDATE shipping.Shipment SET CurrentStatus = @st, CurrentVersion = @v,
                    TerminalStatus = CASE WHEN @terminal = 1 THEN @st ELSE TerminalStatus END,
                    TerminalEventId = CASE WHEN @terminal = 1 THEN @e ELSE TerminalEventId END
                WHERE ShipmentId = @s AND TerminalStatus IS NULL AND CurrentVersion < @v;
                """, ct, ("@st", e.Status), ("@v", e.Version), ("@terminal", result == CarrierStatusOutcomes.TerminalApplied), ("@e", e.EventId), ("@s", e.ShipmentId));
        }

        await tx.CommitAsync(ct);
        LogIngested(logger, e.EventId, e.ShipmentId, e.Status, e.Version, e.Source, result);
        return new CarrierStatusIngested(e.EventId, result, false, result == CarrierStatusOutcomes.TerminalApplied ? e.Status : terminal);
    }

    private static async Task Exec(SqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters) =>
        await Exec(connection, null, sql, ct, parameters);

    private static async Task Exec(SqlConnection connection, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static Task<T> ScalarAsync<T>(SqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters) =>
        ScalarAsync<T>(connection, null, sql, ct, parameters);

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "shipment {ShipmentId}: dispatch attempt {Attempt} → {Answer} ({Detail})")]
    private static partial void LogDispatch(ILogger logger, Guid shipmentId, int attempt, string answer, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "carrier event {EventId} for {ShipmentId} ({Status} v{Version}, {Source}): {Outcome}")]
    private static partial void LogIngested(ILogger logger, Guid eventId, Guid shipmentId, string status, int version, string source, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "shipment {ShipmentId}: carrier status query failed ({Error})")]
    private static partial void LogPollFailed(ILogger logger, Guid shipmentId, string error);
}
