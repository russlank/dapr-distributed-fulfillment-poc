using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fulfillment.Contracts.Reviews;
using Fulfillment.OrderApi.Persistence;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fulfillment.OrderApi.Reviews;

/// <summary>
/// Operator review API (operator role key, distinct from the customer key). Decisions follow
/// <see cref="ReviewDecisions"/>; the response contract: 200 accepted or replayed, 400 invalid,
/// 401 missing or wrong key, 403 operator not authorized, 404 unknown review, 409
/// review-already-decided | review-expired | review-superseded, 422 decision-id-reused.
/// </summary>
public static partial class ReviewEndpoints
{
    public const string ApiKeyVariable = "OPERATOR_API_KEY";

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static void MapReviews(this WebApplication app)
    {
        var reviews = app.MapGroup(ReviewRoutes.Reviews).RequireApiKey(ApiKeyVariable);
        reviews.MapPost("/{reviewId:guid}/decision", DecideAsync);
        reviews.MapGet("/{reviewId:guid}", GetAsync);
        reviews.MapGet("/", ListAsync);
    }

    /// <summary>Contract checks of a decision; null when valid.</summary>
    public static string? Invalid(SubmitDecisionRequest? request)
    {
        if (request is null || request.DecisionId == Guid.Empty)
        {
            return "decisionId: a non-empty GUID is required";
        }

        if (request.OperatorId is null || !OperatorPattern().IsMatch(request.OperatorId))
        {
            return "operatorId: 1-64 letters, digits, '.', '_' or '-'";
        }

        if (request.Outcome is not (DecisionOutcomes.Approve or DecisionOutcomes.Reject))
        {
            return "outcome: approve or reject";
        }

        return request.Comment is { Length: > 500 } ? "comment: at most 500 characters" : null;
    }

    private static async Task<IResult> DecideAsync(Guid reviewId, HttpRequest http, ReviewDecisions decisions, IOptions<ReviewOptions> options, CancellationToken ct)
    {
        SubmitDecisionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SubmitDecisionRequest>(http.Body, Strict, ct);
        }
        catch (JsonException)
        {
            request = null;
        }

        var invalid = Invalid(request);
        if (invalid is not null)
        {
            return Problem(400, ReviewErrorCodes.InvalidDecision, invalid);
        }

        if (!options.Value.IsOperator(request!.OperatorId))
        {
            return Problem(403, ReviewErrorCodes.OperatorNotAuthorized, "the operator is not authorized to decide reviews");
        }

        DecisionResult result;
        try
        {
            result = await decisions.DecideAsync(reviewId, request, ct);
        }
        catch (SqlException e) when (e.Number is 2627 or 2601)
        {
            // The same decision id committed concurrently for another review.
            result = new DecisionResult(DecisionOutcomeKind.DecisionIdReused, null);
        }

        return result.Kind switch
        {
            DecisionOutcomeKind.Accepted => Results.Ok(new DecisionAccepted(reviewId, request.DecisionId, result.Outcome!, Replayed: false)),
            DecisionOutcomeKind.Replayed => Results.Ok(new DecisionAccepted(reviewId, request.DecisionId, result.Outcome!, Replayed: true)),
            DecisionOutcomeKind.DecisionIdReused => Problem(422, ReviewErrorCodes.DecisionIdReused, "the decision id was used with a different decision"),
            DecisionOutcomeKind.AlreadyDecided => Problem(409, ReviewErrorCodes.ReviewAlreadyDecided, "the review was already decided"),
            DecisionOutcomeKind.Superseded => Problem(409, ReviewErrorCodes.ReviewSuperseded, "the review round expired and a new round is open"),
            DecisionOutcomeKind.Expired => Problem(409, ReviewErrorCodes.ReviewExpired, "the review deadline has passed"),
            _ => Problem(404, ReviewErrorCodes.ReviewNotFound, "no such review"),
        };
    }

    private static async Task<IResult> GetAsync(Guid reviewId, OrderDbContext db, CancellationToken ct)
    {
        var review = await db.ReviewTasks.AsNoTracking().SingleOrDefaultAsync(r => r.ReviewId == reviewId, ct);
        return review is null ? Results.NotFound() : Results.Ok(View(review));
    }

    /// <summary>Reviews, newest first; filter by order and status (for simulated operators).</summary>
    private static async Task<IResult> ListAsync(Guid? orderId, string? status, OrderDbContext db, CancellationToken ct)
    {
        var query = db.ReviewTasks.AsNoTracking().AsQueryable();
        if (orderId is not null)
        {
            query = query.Where(r => r.OrderId == orderId);
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(r => r.Status == status);
        }

        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(rows.Select(View).ToList());
    }

    private static ReviewView View(Domain.ReviewTask r) => new(r.ReviewId, r.OrderId, r.Purpose, r.Round, r.Status,
        DateTime.SpecifyKind(r.DeadlineAt, DateTimeKind.Utc), r.DecidedAt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null, r.DecisionId);

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex OperatorPattern();
}
