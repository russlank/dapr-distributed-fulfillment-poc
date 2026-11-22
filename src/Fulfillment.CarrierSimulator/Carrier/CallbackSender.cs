using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fulfillment.Contracts.Shipping;
using Microsoft.Data.SqlClient;

namespace Fulfillment.CarrierSimulator.Carrier;

/// <summary>
/// Sends due callbacks to the order API's ingress, signed with the shared HMAC key; the receivers are used in turn.
/// An answer from the receiver ends the callback (2xx accepted; 4xx refused, also recorded); no answer or a 5xx is
/// retried on the retry schedule, then the callback is dead-lettered. Every attempt is logged with its answer.
/// </summary>
public sealed partial class CallbackSender(string connectionString, CarrierOptions options, IHttpClientFactory http, ILogger<CallbackSender> logger) : BackgroundService
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Fulfillment.CarrierSimulator");
    private static readonly System.Diagnostics.Metrics.Counter<long> DeadLettered = Meter.CreateCounter<long>("carrier_callbacks_dead_lettered_total",
        description: "callbacks the carrier gave up on after its retries");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string[] targets = options.CallbackUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private readonly int[] retries = [.. options.RetrySchedule.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
    private readonly string key = Environment.GetEnvironmentVariable(CarrierCallbackHeaders.HmacKeyVariable) ?? "";
    private long sends;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await SendNextAsync(stoppingToken))
                {
                }
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException)
            {
                LogFailed(logger, e.Message);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), stoppingToken);
        }
    }

    /// <summary>Claims one due callback (skipping rows another sender holds), sends it, records the answer. False when none is due.</summary>
    private async Task<bool> SendNextAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        long id;
        CarrierCallback callback;
        int attempts;
        bool badSignature;
        var lost = false;
        await using (var pick = new SqlCommand(
                         """
                         SELECT TOP (1) Id, EventId, ShipmentId, OrderId, Status, Version, OccurredAt, Attempts, BadSignature,
                                (SELECT b.Mode FROM carrier.Behavior b WHERE b.ShipmentId = c.ShipmentId)
                         FROM carrier.Callback c WITH (UPDLOCK, READPAST, ROWLOCK)
                         WHERE State = 'Scheduled' AND DueAt <= SYSUTCDATETIME() ORDER BY DueAt, Id;
                         """, connection, tx))
        {
            await using var reader = await pick.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return false;
            }

            id = reader.GetInt64(0);
            callback = new CarrierCallback(reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetInt32(5),
                DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc));
            attempts = reader.GetInt32(7);
            badSignature = reader.GetBoolean(8);
            lost = !reader.IsDBNull(9) && reader.GetString(9) is "Lost" or "LostNoPoll";
        }

        var target = targets[(int)(Interlocked.Increment(ref sends) % targets.Length)];
        var body = JsonSerializer.Serialize(callback, Json);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = badSignature ? new string('0', 64) : CarrierSignature.Sign(key, timestamp, body);
        int? code = null;
        string? detail = null;
        try
        {
            if (lost)
            {
                // Drill: the callback never reaches a receiver (an attempt without an answer), until it is dead-lettered.
                throw new HttpRequestException("dropped (test mode Lost)");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(target), CarrierCallbackHeaders.Route))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(CarrierCallbackHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(CarrierCallbackHeaders.Signature, signature);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.CreateClient("callbacks").SendAsync(request, timeout.Token);
            code = (int)response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            detail = e.GetType().Name + ": " + e.Message;
        }

        attempts++;
        var answered = code is >= 200 and < 500;
        var deadLetter = !answered && attempts > retries.Length;
        var next = !answered && !deadLetter ? retries[attempts - 1] : 0;
        await CarrierOperations.Exec(connection, tx,
            """
            UPDATE carrier.Callback SET Attempts = @a, LastResponse = @c,
                State = CASE WHEN @answered = 1 THEN 'Sent' WHEN @dead = 1 THEN 'DeadLettered' ELSE 'Scheduled' END,
                DueAt = CASE WHEN @answered = 0 AND @dead = 0 THEN DATEADD(second, @next, SYSUTCDATETIME()) ELSE DueAt END
            WHERE Id = @id;
            INSERT carrier.Send (CallbackId, EventId, Attempt, Target, ResponseCode, Detail) VALUES (@id, @e, @a, @t, @c, @d);
            """, ct, ("@a", attempts), ("@c", (object?)code ?? DBNull.Value), ("@answered", answered), ("@dead", deadLetter), ("@next", next), ("@id", id),
            ("@e", callback.EventId), ("@t", target), ("@d", (object?)(detail is { Length: > 400 } d ? d[..400] : detail) ?? DBNull.Value));
        await tx.CommitAsync(ct);
        if (deadLetter)
        {
            DeadLettered.Add(1);
        }

        LogSent(logger, callback.EventId, callback.ShipmentId, callback.Status, callback.Version, attempts, code);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "carrier callback {EventId} for {ShipmentId} ({Status} v{Version}) attempt {Attempt}: {Code}")]
    private static partial void LogSent(ILogger logger, Guid eventId, Guid shipmentId, string status, int version, int attempt, int? code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "carrier callback sender: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);
}
