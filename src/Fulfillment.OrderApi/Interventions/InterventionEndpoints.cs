using Fulfillment.Contracts.Workflows;
using Fulfillment.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Fulfillment.OrderApi.Interventions;

/// <summary>Operator resolution of intervention cases (operator key).</summary>
public static class InterventionEndpoints
{
    public static void MapInterventions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var cases = app.MapGroup(InterventionRoutes.Interventions).RequireApiKey("OPERATOR_API_KEY");
        cases.MapPost("/{caseId:guid}/resolution", async (Guid caseId, ResolveCaseRequest request, InterventionResolutions resolutions, CancellationToken ct) =>
        {
            if (request is null || request.ResolutionId == Guid.Empty || string.IsNullOrWhiteSpace(request.OperatorId) || string.IsNullOrWhiteSpace(request.Action)
                || request.Evidence is null || request.Evidence.Length > 2000)
            {
                return Problem(400, "invalid-resolution", "resolutionId, operatorId, action, and evidence (at most 2000 characters) are required");
            }

            var outcome = await resolutions.ResolveAsync(caseId, request, ct);
            return outcome.Kind switch
            {
                ResolveOutcomeKind.Resolved or ResolveOutcomeKind.Replayed => Results.Ok(outcome.Resolved),
                ResolveOutcomeKind.AlreadyResolved => Problem(409, "case-already-resolved", "the case was resolved by another resolution"),
                ResolveOutcomeKind.ResolutionIdReused => Problem(422, "resolution-id-reused", "the resolution id was used with another payload"),
                ResolveOutcomeKind.InvalidAction => Problem(409, "invalid-action", "the action is not allowed for this case"),
                ResolveOutcomeKind.WorkflowStateUnknown => Problem(503, "workflow-state-unknown", "the workflow host could not be asked; retry"),
                _ => Problem(404, "case-not-found", "no such case"),
            };
        });
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(new ProblemDetails { Status = status, Title = code, Detail = detail, Extensions = { ["code"] = code } });
}
