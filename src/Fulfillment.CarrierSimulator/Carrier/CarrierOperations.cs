using System.Security.Cryptography;
using System.Text;
using Fulfillment.Contracts.Shipping;
using Microsoft.Data.SqlClient;

namespace Fulfillment.CarrierSimulator.Carrier;

public sealed class CarrierOptions
{
    public const string Section = "CarrierSim";

    /// <summary>Time between automatic transitions (PickedUp, InTransit, Delivered).</summary>
    public TimeSpan Step { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Receivers of callbacks (the order API replicas), used in turn per send attempt.</summary>
    public string CallbackUrls { get; set; } = "http://order-api:8080";

    /// <summary>Delays between send attempts of one callback; after the last one it is dead-lettered.</summary>
    public string RetrySchedule { get; set; } = "1,5,30,120,600";

    public static void ForEnvironment(CarrierOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.Step = TimeSpan.FromSeconds(2);
            options.RetrySchedule = "1,2,4,8";
        }
    }
}

public sealed record AcceptOutcome(CarrierShipmentAccepted? Accepted, bool Conflict);

/// <summary>A scripted callback (drill test control).</summary>
/// <param name="SameEventIdAs">Reuse the event id of an earlier scripted step (index), for repeats and payload conflicts.</param>
public sealed record ScriptedCallback(string Status, int Version, int DelayMs, bool Truth = true, bool BadSignature = false, Guid? OrderIdOverride = null,
    int? SameEventIdAs = null, Guid? EventId = null);

/// <summary>The carrier's records as guarded SQL: acceptance by shipment id (stored before answering), status, and the callback schedule.</summary>
public sealed class CarrierOperations(string connectionString, CarrierOptions options)
{
    public async Task<AcceptOutcome> AcceptAsync(CarrierShipmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{request.OrderId:N}|{request.Parcels}"));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await using (var read = new SqlCommand("SELECT PayloadHash, AcceptedAt FROM carrier.Shipment WITH (UPDLOCK, HOLDLOCK) WHERE ShipmentId = @s", connection, tx))
        {
            read.Parameters.AddWithValue("@s", request.ShipmentId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var same = ((byte[])reader[0]).AsSpan().SequenceEqual(hash);
                var acceptedAt = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                await reader.DisposeAsync();
                if (!same)
                {
                    await tx.RollbackAsync(ct);
                    return new AcceptOutcome(null, true);
                }

                await Exec(connection, tx, "UPDATE carrier.Shipment SET Replays = Replays + 1 WHERE ShipmentId = @s", ct, ("@s", request.ShipmentId));
                await tx.CommitAsync(ct);
                return new AcceptOutcome(new CarrierShipmentAccepted(request.ShipmentId, acceptedAt, true), false);
            }
        }

        await Exec(connection, tx, "INSERT carrier.Shipment (ShipmentId, OrderId, Parcels, PayloadHash, Replays) VALUES (@s, @o, @p, @h, 0)", ct,
            ("@s", request.ShipmentId), ("@o", request.OrderId), ("@p", request.Parcels), ("@h", hash));
        var manual = await Scalar<string>(connection, tx, "SELECT Mode FROM carrier.Behavior WHERE ShipmentId = @s", ct, ("@s", request.ShipmentId)) == "Manual";
        if (!manual)
        {
            var steps = new[] { CarrierStatuses.PickedUp, CarrierStatuses.InTransit, CarrierStatuses.Delivered };
            for (var i = 0; i < steps.Length; i++)
            {
                await InsertCallbackAsync(connection, tx, Guid.NewGuid(), request.ShipmentId, request.OrderId, steps[i], i + 1,
                    (int)(options.Step.TotalMilliseconds * (i + 1)), truth: true, badSignature: false, label: null, ct);
            }
        }

        var accepted = DateTime.SpecifyKind(await Scalar<DateTime>(connection, tx, "SELECT AcceptedAt FROM carrier.Shipment WHERE ShipmentId = @s", ct, ("@s", request.ShipmentId)), DateTimeKind.Utc);
        await tx.CommitAsync(ct);
        return new AcceptOutcome(new CarrierShipmentAccepted(request.ShipmentId, accepted, false), false);
    }

    /// <summary>The carrier's latest transition of the shipment (its own record, not drill anomalies); null if unknown.</summary>
    public async Task<CarrierShipmentStatus?> StatusAsync(Guid shipmentId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var read = new SqlCommand(
            """
            SELECT TOP (1) EventId, Status, Version, OccurredAt FROM carrier.Callback
            WHERE ShipmentId = @s AND IsTruth = 1 AND OccurredAt <= SYSUTCDATETIME() ORDER BY Version DESC;
            """, connection);
        read.Parameters.AddWithValue("@s", shipmentId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new CarrierShipmentStatus(shipmentId, reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        }

        await reader.DisposeAsync();
        var known = await Scalar<int>(connection, null, "SELECT COUNT(*) FROM carrier.Shipment WHERE ShipmentId = @s", ct, ("@s", shipmentId));
        return known == 0 ? null : new CarrierShipmentStatus(shipmentId, Guid.Empty, CarrierStatuses.Accepted, 0, DateTime.UtcNow);
    }

    public async Task SetModeAsync(Guid shipmentId, string mode, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await Exec(connection, null,
            "MERGE carrier.Behavior AS t USING (SELECT @s AS ShipmentId) AS s ON t.ShipmentId = s.ShipmentId WHEN MATCHED THEN UPDATE SET Mode = @m WHEN NOT MATCHED THEN INSERT (ShipmentId, Mode) VALUES (@s, @m);",
            ct, ("@s", shipmentId), ("@m", mode));
    }

    /// <summary>Schedules scripted callbacks for a shipment; returns the event id of each step.</summary>
    public async Task<IReadOnlyList<Guid>> ScriptAsync(Guid shipmentId, string label, IReadOnlyList<ScriptedCallback> steps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var orderId = await Scalar<Guid?>(connection, null, "SELECT OrderId FROM carrier.Shipment WHERE ShipmentId = @s", ct, ("@s", shipmentId)) ?? shipmentId;
        var ids = new List<Guid>();
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var step in steps)
        {
            var eventId = step.EventId ?? (step.SameEventIdAs is { } i ? ids[i] : Guid.NewGuid());
            ids.Add(eventId);
            await InsertCallbackAsync(connection, tx, eventId, shipmentId, step.OrderIdOverride ?? orderId, step.Status, step.Version, step.DelayMs,
                step.Truth, step.BadSignature, label, ct);
        }

        await tx.CommitAsync(ct);
        return ids;
    }

    private static async Task InsertCallbackAsync(SqlConnection connection, SqlTransaction? tx, Guid eventId, Guid shipmentId, Guid orderId, string status, int version,
        int delayMs, bool truth, bool badSignature, string? label, CancellationToken ct) =>
        await Exec(connection, tx,
            """
            INSERT carrier.Callback (EventId, ShipmentId, OrderId, Status, Version, OccurredAt, DueAt, State, Attempts, IsTruth, BadSignature, Label)
            VALUES (@e, @s, @o, @st, @v, DATEADD(millisecond, @d, SYSUTCDATETIME()), DATEADD(millisecond, @d, SYSUTCDATETIME()), 'Scheduled', 0, @t, @b, @l);
            """, ct, ("@e", eventId), ("@s", shipmentId), ("@o", orderId), ("@st", status), ("@v", version), ("@d", delayMs), ("@t", truth), ("@b", badSignature),
            ("@l", (object?)label ?? DBNull.Value));

    internal static async Task Exec(SqlConnection connection, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<T> Scalar<T>(SqlConnection connection, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value2 = await command.ExecuteScalarAsync(ct);
        return value2 is null or DBNull ? default! : (T)value2;
    }
}
