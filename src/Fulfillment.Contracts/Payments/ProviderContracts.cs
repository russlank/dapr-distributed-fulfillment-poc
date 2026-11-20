namespace Fulfillment.Contracts.Payments;

/// <summary>
/// HTTP contract of the external payment provider (simulated by Fulfillment.PaymentProviderSimulator; no Dapr).
/// Every operation is identified by the caller's operation key, which is the provider's idempotency key.
/// </summary>
public static class ProviderRoutes
{
    public const string Operations = "/provider/operations";
    public const string ApiKeyVariable = "PROVIDER_API_KEY";
}

public static class ProviderKinds
{
    public const string Authorize = "Authorize";
    public const string Capture = "Capture";
    public const string Void = "Void";
    public const string Refund = "Refund";
}

/// <summary>Committed results. Business outcomes are results, not HTTP errors.</summary>
public static class ProviderResults
{
    public const string Authorized = "Authorized";
    public const string Declined = "Declined";
    public const string Captured = "Captured";
    public const string Voided = "Voided";
    public const string Refunded = "Refunded";

    /// <summary>The referenced operation never committed: nothing to compensate; its key is now prohibited.</summary>
    public const string NothingToVoid = "NothingToVoid";

    /// <summary>The referenced capture never committed: nothing to refund; its key is now prohibited.</summary>
    public const string NothingToRefund = "NothingToRefund";

    /// <summary>A business rule refused the operation (code in <see cref="ProviderOperationResponse.ResultCode"/>).</summary>
    public const string Rejected = "Rejected";
}

public static class ProviderResultCodes
{
    public const string AuthorizationNotFound = "AuthorizationNotFound";
    public const string AuthorizationNotUsable = "AuthorizationNotUsable";
    public const string AuthorizationVoided = "AuthorizationVoided";
    public const string AuthorizationExpired = "AuthorizationExpired";
    public const string AlreadyCaptured = "AlreadyCaptured";
    public const string AmountMismatch = "AmountMismatch";
    public const string CaptureNotUsable = "CaptureNotUsable";

    /// <summary>On NothingToVoid/NothingToRefund: the referenced key never committed and is now prohibited.</summary>
    public const string ReferenceNeverCommitted = "ReferenceNeverCommitted";
}

/// <summary>Answer states of a request or status query.</summary>
public static class ProviderStatuses
{
    public const string Committed = "Committed";

    /// <summary>The key can never commit: a void or refund named it first (see <see cref="ProviderOperationResponse.ProhibitedBy"/>).</summary>
    public const string Prohibited = "Prohibited";

    public const string NotFound = "NotFound";

    /// <summary>The provider cannot tell (a fault mode of the simulator).</summary>
    public const string Unknown = "Unknown";
}

/// <param name="Reference">The operation key this one acts on: the authorization for Capture and Void, the capture for Refund.</param>
public sealed record ProviderOperationRequest(string OperationKey, string Kind, long AmountMinor, string Currency, string? Reference);

/// <param name="Status">Committed, Prohibited, NotFound, or Unknown.</param>
/// <param name="Replayed">The key was already committed with the same payload; this is the stored result.</param>
public sealed record ProviderOperationResponse(
    string OperationKey,
    string Status,
    string? Kind = null,
    string? Result = null,
    string? ResultCode = null,
    long AmountMinor = 0,
    string? Currency = null,
    string? Reference = null,
    DateTime? AuthorizationExpiresAtUtc = null,
    DateTime? CommittedAtUtc = null,
    string? ProhibitedBy = null,
    bool Replayed = false);
