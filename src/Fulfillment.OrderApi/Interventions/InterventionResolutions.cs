using Fulfillment.Contracts.Workflows;
using Fulfillment.OrderApi.Domain;
using Fulfillment.OrderApi.Orders;
using Microsoft.Data.SqlClient;

namespace Fulfillment.OrderApi.Interventions;

public enum ResolveOutcomeKind
{
    Resolved,
    Replayed,
    NotFound,

    /// <summary>The case was already resolved by another resolution id.</summary>
    AlreadyResolved,

    /// <summary>The action is not allowed for the case's kind or state.</summary>
    InvalidAction,

    /// <summary>The resolution id was used with another payload or case.</summary>
    ResolutionIdReused,

    /// <summary>The workflow host could not be asked whether the original workflow waits; retry later.</summary>
    WorkflowStateUnknown,
}

public sealed record ResolveOutcome(ResolveOutcomeKind Kind, CaseResolved? Resolved = null);

/// <summary>
/// Resolution of an intervention case, in one OrderDb transaction (plan Section 7.4): the case becomes Resolved with
/// the action, evidence, and operator, and exactly one path is taken:
/// <list type="bullet">
/// <item>Signal: the original workflow is running and waits → the order returns to the status it had before the case,
/// and a pending signal wakes the workflow (which re-reads the case and the external record).</item>
/// <item>Repair: no workflow waits (terminal, failed, or missing) → a repair start request for repair-{order}-{case};
/// the repair workflow runs the effects with repair operation keys.</item>
/// <item>RecordOnly: a case on a terminal order (for example a conflicting carrier status after completion).</item>
/// </list>
/// Whether the workflow is running is asked from the workflow host before the transaction; a repeated resolution id
/// returns the stored resolution.
/// </summary>
public sealed class InterventionResolutions(string connectionString, WorkflowHostClient workflows)
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [InterventionKinds.AmbiguousPayment] = [CaseActions.ConfirmExternalSuccess, CaseActions.ConfirmExternalFailure],
        [InterventionKinds.CompensationExhausted] = [CaseActions.ConfirmExternalSuccess, CaseActions.ConfirmExternalFailure, CaseActions.Acknowledge],
        [InterventionKinds.AmbiguousDispatch] = [CaseActions.ConfirmExternalSuccess, CaseActions.ConfirmExternalFailure],
        [InterventionKinds.DeliveryException] = [CaseActions.ConfirmExternalSuccess, CaseActions.ConfirmExternalFailure, CaseActions.Acknowledge],
        [InterventionKinds.DeliveryOverdue] = [CaseActions.ConfirmExternalSuccess, CaseActions.ConfirmExternalFailure, CaseActions.Acknowledge],
        [InterventionKinds.CarrierTerminalConflict] = [CaseActions.Acknowledge],

        // Reconciler cases: confirm the failure (compensate if still allowed), confirm success after checking the external
        // record (a mismatch with a delivered shipment is finalized), or acknowledge (record only).
        [InterventionKinds.WorkflowFaulted] = [CaseActions.ConfirmExternalFailure, CaseActions.Acknowledge],
        [InterventionKinds.WorkflowMissing] = [CaseActions.ConfirmExternalFailure, CaseActions.Acknowledge],
        [InterventionKinds.ExecutionMismatch] = [CaseActions.ConfirmExternalSuccess, CaseActions.ConfirmExternalFailure, CaseActions.Acknowledge],
        [InterventionKinds.ProjectionGap] = [CaseActions.Acknowledge],
    };

    public async Task<ResolveOutcome> ResolveAsync(Guid caseId, ResolveCaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var known = await ReadAsync(caseId, ct);
        if (known is null)
        {
            return new ResolveOutcome(ResolveOutcomeKind.NotFound);
        }

        // Asked before the transaction: is the original workflow still there to be woken?
        var running = false;
        if (known.Value.ResumeStatus is not null && known.Value.State is CaseStates.Open or CaseStates.Reconciling)
        {
            try
            {
                var state = await workflows.GetStateAsync(FulfillmentWorkflow.InstanceId(known.Value.OrderId), ct);
                running = state.Exists && state.Status is "Running" or "Pending" or "Suspended";
            }
            catch (WorkflowHostUnavailableException)
            {
                return new ResolveOutcome(ResolveOutcomeKind.WorkflowStateUnknown);
            }
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await OrderTransitions.LockAsync(connection, tx, known.Value.OrderId, ct);
        var current = (await ReadAsync(connection, tx, caseId, ct))!.Value;
        if (current.State == CaseStates.Resolved)
        {
            await tx.CommitAsync(ct);
            if (current.ResolutionId == request.ResolutionId)
            {
                return current.Action == request.Action
                    ? new ResolveOutcome(ResolveOutcomeKind.Replayed, new CaseResolved(caseId, current.Action!, current.Path!, true))
                    : new ResolveOutcome(ResolveOutcomeKind.ResolutionIdReused);
            }

            return new ResolveOutcome(ResolveOutcomeKind.AlreadyResolved, new CaseResolved(caseId, current.Action!, current.Path!, false));
        }

        if (!Allowed.TryGetValue(current.Kind, out var actions) || !actions.Contains(request.Action))
        {
            await tx.RollbackAsync(ct);
            return new ResolveOutcome(ResolveOutcomeKind.InvalidAction);
        }

        var orderStatus = Enum.Parse<OrderStatus>(current.OrderStatus);
        var path = current.ResumeStatus is null || OrderStatuses.IsTerminal(orderStatus) ? CasePaths.RecordOnly
            : running ? CasePaths.Signal
            : CasePaths.Repair;
        await Exec(connection, tx,
            """
            UPDATE orders.InterventionCase SET State = 'Resolved', ResolutionAction = @a, ResolutionPath = @p, ResolutionId = @rid,
                ResolutionEvidence = LEFT(@e, 2000), ResolvedBy = LEFT(@by, 64), ResolvedAt = SYSUTCDATETIME()
            WHERE CaseId = @c AND State IN ('Open', 'Reconciling');
            """, ct, ("@a", request.Action), ("@p", path), ("@rid", request.ResolutionId), ("@e", request.Evidence), ("@by", request.OperatorId), ("@c", caseId));

        if (path == CasePaths.Signal)
        {
            var resume = Enum.Parse<OrderStatus>(current.ResumeStatus!);
            if (orderStatus == OrderStatus.ManualInterventionRequired)
            {
                await OrderTransitions.TryTransitionAsync(connection, tx, current.OrderId, OrderStatus.ManualInterventionRequired, resume,
                    OrderReasonCodes.InterventionResolved, StepCheckpoints.None, $"{caseId:N}:resolved", ct);
            }

            await Exec(connection, tx,
                """
                INSERT orders.PendingSignal (OrderId, InstanceId, EventName, CausedBy, State, Attempts)
                SELECT @o, @i, @n, @rid, 'Pending', 0 WHERE NOT EXISTS (SELECT 1 FROM orders.PendingSignal WHERE CausedBy = @rid AND EventName = @n);
                """, ct, ("@o", current.OrderId), ("@i", FulfillmentWorkflow.InstanceId(current.OrderId)), ("@n", InterventionSignals.EventName(caseId)),
                ("@rid", request.ResolutionId));
        }
        else if (path == CasePaths.Repair)
        {
            await Exec(connection, tx,
                """
                INSERT orders.RepairStartRequest (CaseId, OrderId, InstanceId, State, Attempts)
                SELECT @c, @o, @i, 'Pending', 0 WHERE NOT EXISTS (SELECT 1 FROM orders.RepairStartRequest WHERE CaseId = @c);
                """, ct, ("@c", caseId), ("@o", current.OrderId), ("@i", RepairWorkflow.InstanceId(current.OrderId, caseId)));
        }

        await tx.CommitAsync(ct);
        return new ResolveOutcome(ResolveOutcomeKind.Resolved, new CaseResolved(caseId, request.Action, path, false));
    }

    /// <summary>Records the repair workflow's outcome on its case (once).</summary>
    public async Task RecordRepairOutcomeAsync(RepairOutcomeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await Exec(connection, null,
            """
            UPDATE orders.InterventionCase SET RepairOutcome = @out WHERE CaseId = @c AND RepairOutcome IS NULL;
            UPDATE orders.RepairStartRequest SET State = 'Closed', ClosedAt = SYSUTCDATETIME(), LeaseOwner = NULL, LeaseToken = NULL, LeaseExpiresAt = NULL
            WHERE CaseId = @c AND State <> 'Closed';
            """, ct, ("@out", request.Outcome), ("@c", request.CaseId));
    }

    private readonly record struct CaseRow(Guid OrderId, string Kind, string State, string? ResumeStatus, string OrderStatus, Guid? ResolutionId, string? Action, string? Path);

    private async Task<CaseRow?> ReadAsync(Guid caseId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return await ReadAsync(connection, null, caseId, ct);
    }

    private static async Task<CaseRow?> ReadAsync(SqlConnection connection, SqlTransaction? tx, Guid caseId, CancellationToken ct)
    {
        await using var read = new SqlCommand(
            """
            SELECT c.OrderId, c.Kind, c.State, c.ResumeStatus, o.Status, c.ResolutionId, c.ResolutionAction, c.ResolutionPath
            FROM orders.InterventionCase c JOIN orders.[Order] o ON o.OrderId = c.OrderId WHERE c.CaseId = @c;
            """, connection, tx);
        read.Parameters.AddWithValue("@c", caseId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CaseRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;
    }

    private static async Task Exec(SqlConnection connection, SqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}
