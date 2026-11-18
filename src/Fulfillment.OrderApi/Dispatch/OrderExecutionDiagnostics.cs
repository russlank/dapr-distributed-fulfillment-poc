using Fulfillment.OrderApi.Persistence;
using Fulfillment.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Fulfillment.OrderApi.Dispatch;

/// <summary>
/// Operational view of one order's workflow execution (admin key): the start request, its attempts,
/// the execution claims, and the workflow's runtime state as the workflow host reports it. Read-only;
/// for operators and test scripts. The customer API never shows execution state.
/// </summary>
public static class OrderExecutionDiagnostics
{
    public static void MapOrderExecutionDiagnostics(this WebApplication app)
    {
        app.MapGet("/system/orders/{orderId:guid}/execution", async (Guid orderId, OrderDbContext db, WorkflowHostClient workflows, CancellationToken ct) =>
        {
            var request = await db.WorkflowStartRequests.AsNoTracking().SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
            if (request is null)
            {
                return Results.NotFound();
            }

            var attempts = await db.WorkflowStartAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.Id)
                .Select(a => new { a.Attempt, a.DispatcherId, a.CalledAt, a.CompletedAt, a.Result, a.ReportedStatus }).ToListAsync(ct);
            var claims = await db.ExecutionClaims.AsNoTracking().Where(c => c.OrderId == orderId).OrderBy(c => c.Id)
                .Select(c => new { c.Outcome, c.OrderStatus, c.ClaimedAt }).ToListAsync(ct);

            object? workflow;
            try
            {
                workflow = await workflows.GetStateAsync(request.InstanceId, ct);
            }
            catch (WorkflowHostUnavailableException)
            {
                workflow = null;
            }

            return Results.Ok(new
            {
                startRequest = new
                {
                    request.InstanceId,
                    State = request.State.ToString(),
                    request.Attempts,
                    request.LeaseOwner,
                    request.LeaseExpiresAt,
                    request.NextAttemptAt,
                    request.LastErrorCode,
                    request.StartedAt,
                    request.ClosedAt,
                    request.ExecutionClaims,
                    request.FenceRejections,
                },
                attempts,
                claims,
                workflow,
            });
        }).RequireApiKey("ADMIN_API_KEY");
    }
}
