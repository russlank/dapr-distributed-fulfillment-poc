using Fulfillment.Contracts.Shipping;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;

namespace Fulfillment.CarrierSimulator.Carrier;

/// <summary>Failpoints of the carrier (drill builds only).</summary>
public static class CarrierFailpoints
{
    /// <summary>After the shipment and its callback schedule are committed, before the answer (key: shipment id, N format).</summary>
    public const string AfterCarrierAcceptCommitBeforeResponse = "AfterCarrierAcceptCommitBeforeResponse";
}

public static class CarrierEndpoints
{
    public static void MapCarrierEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var shipments = app.MapGroup(CarrierRoutes.Shipments).RequireApiKey(CarrierRoutes.ApiKeyVariable);
        shipments.MapPost("/", async (CarrierShipmentRequest request, CarrierOperations ops, Failpoints failpoints, CancellationToken ct) =>
        {
            if (request is null || request.ShipmentId == Guid.Empty || request.OrderId == Guid.Empty || request.Parcels is < 1 or > 100)
            {
                return Results.BadRequest(new { error = "invalid-shipment" });
            }

            var outcome = await ops.AcceptAsync(request, ct);
            if (outcome.Conflict)
            {
                return Results.Conflict(new { error = "shipment-id-reused", shipmentId = request.ShipmentId });
            }

            await failpoints.HitAsync(CarrierFailpoints.AfterCarrierAcceptCommitBeforeResponse, request.ShipmentId.ToString("N"), ct);
            return Results.Ok(outcome.Accepted);
        });
        shipments.MapGet("/{shipmentId:guid}", async (Guid shipmentId, CarrierOperations ops, CancellationToken ct) =>
            await ops.StatusAsync(shipmentId, ct) is { } status ? Results.Ok(status) : Results.NotFound(new { error = "unknown-shipment" }));

        if (Environment.GetEnvironmentVariable("DAPR_POC_ENV") == "drill")
        {
            var test = app.MapGroup("/carrier/test").RequireApiKey(CarrierRoutes.ApiKeyVariable);
            test.MapPut("/shipments/{shipmentId:guid}/mode/{mode}", async (Guid shipmentId, string mode, CarrierOperations ops, CancellationToken ct) =>
            {
                if (mode is not ("Auto" or "Manual"))
                {
                    return Results.BadRequest(new { error = "invalid-mode" });
                }

                await ops.SetModeAsync(shipmentId, mode, ct);
                return Results.Ok(new { shipmentId, mode });
            });
            test.MapPost("/shipments/{shipmentId:guid}/script", async (Guid shipmentId, string label, ScriptedCallback[] steps, CarrierOperations ops, CancellationToken ct) =>
                steps is { Length: > 0 } && steps.All(s => CarrierStatuses.IsKnown(s.Status) && s.Version is >= 0 and < 1000 && s.DelayMs is >= 0 and < 600_000)
                    ? Results.Ok(new { eventIds = await ops.ScriptAsync(shipmentId, label, steps, ct) })
                    : Results.BadRequest(new { error = "invalid-script" }));
            test.MapGet("/shipments/{shipmentId:guid}/sends", async (Guid shipmentId, IConfiguration configuration, CancellationToken ct) =>
            {
                await using var connection = new SqlConnection(configuration.GetConnectionString("CarrierSimDb"));
                await connection.OpenAsync(ct);
                await using var read = new SqlCommand(
                    """
                    SELECT c.EventId, c.Status, c.Version, c.IsTruth, c.BadSignature, c.State, s.Attempt, s.ResponseCode, s.Target
                    FROM carrier.Callback c LEFT JOIN carrier.Send s ON s.CallbackId = c.Id WHERE c.ShipmentId = @s ORDER BY c.Id, s.Attempt;
                    """, connection);
                read.Parameters.AddWithValue("@s", shipmentId);
                var rows = new List<object>();
                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new
                    {
                        eventId = reader.GetGuid(0),
                        status = reader.GetString(1),
                        version = reader.GetInt32(2),
                        truth = reader.GetBoolean(3),
                        badSignature = reader.GetBoolean(4),
                        state = reader.GetString(5),
                        attempt = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
                        responseCode = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
                        target = reader.IsDBNull(8) ? null : reader.GetString(8),
                    });
                }

                return Results.Ok(new { sends = rows });
            });
        }
    }
}
