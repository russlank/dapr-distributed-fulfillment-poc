namespace Fulfillment.Contracts.Reviews;

/// <summary>Operator review API (HTTP only; the operator role key, never the customer key).</summary>
public static class ReviewRoutes
{
    public const string Reviews = "/reviews";
    public const string ApiKeyHeader = "X-Api-Key";

    public static string Decision(Guid reviewId) => $"/reviews/{reviewId}/decision";
}

/// <summary>Decision outcomes an operator can submit (M1: approve or reject; escalation comes later).</summary>
public static class DecisionOutcomes
{
    public const string Approve = "approve";
    public const string Reject = "reject";
}

/// <summary>Stable problem codes of the review API (the "code" extension of a problem response).</summary>
public static class ReviewErrorCodes
{
    public const string InvalidDecision = "invalid-decision";
    public const string OperatorNotAuthorized = "operator-not-authorized";
    public const string DecisionIdReused = "decision-id-reused";
    public const string ReviewAlreadyDecided = "review-already-decided";
    public const string ReviewExpired = "review-expired";
    public const string ReviewSuperseded = "review-superseded";
    public const string ReviewNotFound = "review-not-found";
}

/// <param name="DecisionId">Caller-chosen id; a retry sends the same id and payload.</param>
/// <param name="OperatorId">Simulated operator identity (must be a configured operator).</param>
/// <param name="Outcome">approve or reject.</param>
/// <param name="Comment">Optional; may be omitted.</param>
public sealed record SubmitDecisionRequest(Guid DecisionId, string OperatorId, string Outcome, string? Comment = null);

/// <param name="Replayed">True when this decision id and payload were accepted before.</param>
public sealed record DecisionAccepted(Guid ReviewId, Guid DecisionId, string Outcome, bool Replayed);

public sealed record ReviewView(Guid ReviewId, Guid OrderId, string Purpose, int Round, string Status, DateTime DeadlineAt, DateTime? DecidedAt, Guid? DecisionId);
