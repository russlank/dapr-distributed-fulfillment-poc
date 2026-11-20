namespace Fulfillment.Contracts.Shipping;

/// <summary>Carrier statuses in their order; Delivered and Exception are terminal.</summary>
public static class CarrierStatuses
{
    public const string Accepted = "Accepted";
    public const string PickedUp = "PickedUp";
    public const string InTransit = "InTransit";
    public const string Delivered = "Delivered";
    public const string Exception = "Exception";

    public static bool IsTerminal(string status) => status is Delivered or Exception;

    public static bool IsKnown(string status) => status is Accepted or PickedUp or InTransit or Delivered or Exception;
}

/// <summary>HTTP contract of the external carrier (simulated by Fulfillment.CarrierSimulator; no Dapr).</summary>
public static class CarrierRoutes
{
    public const string Shipments = "/carrier/shipments";
    public const string ApiKeyVariable = "CARRIER_API_KEY";
}

/// <summary>The shipment request; <paramref name="ShipmentId"/> is the carrier's idempotency key.</summary>
public sealed record CarrierShipmentRequest(Guid ShipmentId, Guid OrderId, int Parcels);

/// <param name="Replayed">The shipment already existed with the same request; this is the stored acceptance.</param>
public sealed record CarrierShipmentAccepted(Guid ShipmentId, DateTime AcceptedAtUtc, bool Replayed);

/// <summary>The carrier's latest transition of a shipment (status query).</summary>
public sealed record CarrierShipmentStatus(Guid ShipmentId, Guid EventId, string Status, int Version, DateTime OccurredAtUtc);

/// <summary>
/// A status callback, sent by the carrier to the order API's ingress, signed with HMAC-SHA256 over
/// "{timestamp}.{body}" (headers <see cref="CarrierCallbackHeaders"/>). One event per carrier transition.
/// </summary>
public sealed record CarrierCallback(Guid EventId, Guid ShipmentId, Guid OrderId, string Status, int Version, DateTime OccurredAtUtc);

public static class CarrierCallbackHeaders
{
    public const string Signature = "X-Carrier-Signature";
    public const string Timestamp = "X-Carrier-Timestamp";
    public const string Route = "/carrier/callbacks";
    public const string HmacKeyVariable = "CARRIER_HMAC_KEY";
}

/// <summary>Outcomes of ingesting one carrier status event in Shipping (stored per event; a repeat returns the stored one).</summary>
public static class CarrierStatusOutcomes
{
    public const string Applied = "Applied";
    public const string TerminalApplied = "TerminalApplied";
    public const string Stale = "Stale";
    public const string Duplicate = "Duplicate";
    public const string TerminalConflict = "TerminalConflict";
    public const string TerminalRepeat = "TerminalRepeat";
    public const string UnknownShipment = "UnknownShipment";
    public const string PayloadConflict = "PayloadConflict";

    /// <summary>The event names a shipment of another order than the shipment belongs to.</summary>
    public const string CorrelationMismatch = "CorrelationMismatch";
}

/// <summary>Shipping's internal ingest call (Dapr service invocation from the order API's forwarder, or Shipping's own poll).</summary>
public static class ShippingRoutes
{
    public const string CarrierStatus = "/internal/carrier-status";
}

/// <param name="Source">callback or poll.</param>
public sealed record CarrierStatusIngest(Guid EventId, Guid ShipmentId, Guid OrderId, string Status, int Version, DateTime OccurredAtUtc, string Source, string PayloadHash);

public sealed record CarrierStatusIngested(Guid EventId, string Outcome, bool Replayed, string? TerminalStatus);

/// <summary>Activities of the shipping service (cross-app activities of the fulfillment workflow).</summary>
public static class ShippingActivities
{
    /// <summary>Records the shipment (once per order) and has the carrier accept it, by shipment id.</summary>
    public const string DispatchShipment = "DispatchShipment";

    /// <summary>Shipping's current view of the shipment (the workflow never trusts a signal's content).</summary>
    public const string GetShipmentStatus = "GetShipmentStatus";

    /// <summary>Asks the carrier for the latest transition and ingests it through the same idempotent function as a callback.</summary>
    public const string PollCarrierStatus = "PollCarrierStatus";
}

public static class DispatchResults
{
    public const string Dispatched = "Dispatched";

    /// <summary>The carrier could not be reached and no request can have reached it.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>A request may have reached the carrier and its outcome is unknown after the budget.</summary>
    public const string Ambiguous = "Ambiguous";
}

public sealed record DispatchShipmentRequest(Guid OrderId, int Parcels);

public sealed record DispatchShipmentResult(string Result, Guid ShipmentId, DateTime? DeliveryDeadlineAtUtc, int Attempts, string Replica);

public sealed record ShipmentStatusRequest(Guid OrderId);

/// <param name="DeadlinePassed">SQL time has reached the delivery deadline.</param>
public sealed record ShipmentStatusResult(Guid ShipmentId, string? CurrentStatus, string? TerminalStatus, DateTime? DeliveryDeadlineAtUtc, bool DeadlinePassed);

public static class CarrierSignals
{
    public static string EventName(Guid shipmentId) => $"CarrierStatus_{shipmentId:N}";
}
