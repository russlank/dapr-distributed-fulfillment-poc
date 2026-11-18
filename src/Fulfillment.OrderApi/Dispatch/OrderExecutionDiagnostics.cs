using Fulfillment.OrderApi.Persistence;
using Fulfillment.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Fulfillment.OrderApi.Dispatch;

/// <summary>
/// Operational view of one order's workflow execution (admin key): status and history, the simulated
/// effects and check results, the start request, its attempts, the execution claims, and the workflow's runtime state as the workflow host reports it. Read-only;
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
            var status = await db.Orders.AsNoTracking().Where(o => o.OrderId == orderId).Select(o => o.Status).SingleAsync(ct);
            var history = await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == orderId).OrderBy(h => h.Sequence)
                .Select(h => new { h.Sequence, h.FromStatus, h.ToStatus, h.ReasonCode, h.OccurredAt }).ToListAsync(ct);
            var effects = await db.StubEffects.AsNoTracking().Where(e => e.OrderId == orderId).OrderBy(e => e.AppliedAt).ThenBy(e => e.OperationKey)
                .Select(e => new { e.OperationKey, e.Step, e.Qualifier, e.AppliedBy, e.AppliedAt, e.Replays }).ToListAsync(ct);
            var checks = await db.OrderChecks.AsNoTracking().Where(c => c.OrderId == orderId).OrderBy(c => c.CheckName)
                .Select(c => new { c.CheckName, c.OperationKey, c.Replica, c.StartedAt, c.CompletedAt, c.TotalMinor, c.Currency, c.RiskScore, c.ReviewRequired, c.RuleVersion, c.Replays })
                .ToListAsync(ct);
            var reviews = await db.ReviewTasks.AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.Round)
                .Select(r => new { r.ReviewId, r.Round, r.Status, r.CreatedAt, r.DeadlineAt, r.DecidedAt, r.DecisionId, r.ExpiredAt }).ToListAsync(ct);
            var reviewIds = reviews.Select(r => r.ReviewId).ToList();
            var decisions = await db.ReviewDecisions.AsNoTracking().Where(d => d.OrderId == orderId)
                .Select(d => new { d.DecisionId, d.ReviewId, d.Outcome, d.OperatorId, d.DecidedAt }).ToListAsync(ct);
            var wakes = await db.ReviewWakes.AsNoTracking().Where(w => reviewIds.Contains(w.ReviewId)).OrderBy(w => w.Id)
                .Select(w => new { w.ReviewId, w.Wake, w.SeenStatus, w.DeadlinePassed, w.At }).ToListAsync(ct);
            var signals = await db.PendingSignals.AsNoTracking().Where(s => s.OrderId == orderId).OrderBy(s => s.CreatedAt)
                .Select(s => new { s.SignalId, s.EventName, s.State, s.Attempts, s.LastErrorCode, s.CreatedAt, s.ForwardedAt, s.ClosedAt }).ToListAsync(ct);
            var signalIds = signals.Select(s => s.SignalId).ToList();
            var signalAttempts = await db.SignalAttempts.AsNoTracking().Where(a => signalIds.Contains(a.SignalId)).OrderBy(a => a.Id)
                .Select(a => new { a.SignalId, a.Attempt, a.DispatcherId, a.CalledAt, a.CompletedAt, a.Result, a.ReportedStatus, a.InvocationAttempts, a.HttpStatus, a.TransportError })
                .ToListAsync(ct);

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
                order = new { Status = status.ToString(), History = history.Select(h => new { h.Sequence, FromStatus = h.FromStatus?.ToString(), ToStatus = h.ToStatus.ToString(), h.ReasonCode, h.OccurredAt }) },
                stubEffects = effects,
                checks,
                reviews,
                decisions,
                reviewWakes = wakes,
                signals,
                signalAttempts,
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
