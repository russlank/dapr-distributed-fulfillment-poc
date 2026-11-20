namespace Fulfillment.OrderApi.Domain;

/// <summary>
/// An operational intervention case in OrderDb, independent of the workflow's lifetime: opened by a workflow
/// activity (for example an undeterminable payment), resolved once by an operator with evidence. One open case
/// per order and kind. Opening it moves a non-terminal order to ManualInterventionRequired (the status before is
/// kept to resume to); on a terminal order it only adds a history row.
/// </summary>
public sealed class InterventionCase
{
    private InterventionCase()
    {
        Kind = null!;
        State = null!;
        OperationKey = null!;
        Evidence = null!;
    }

    public Guid CaseId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Kind { get; private set; }

    /// <summary>Open, Reconciling, Resolved, or Abandoned.</summary>
    public string State { get; private set; }

    /// <summary>Idempotency key of the opening step: a repeated activity returns the same case.</summary>
    public string OperationKey { get; private set; }

    public string Evidence { get; private set; }

    /// <summary>The order's status when the case moved it to ManualInterventionRequired; null when the order was terminal.</summary>
    public string? ResumeStatus { get; private set; }

    public DateTime OpenedAt { get; private set; }

    /// <summary>ConfirmExternalSuccess, ConfirmExternalFailure, or Acknowledge.</summary>
    public string? ResolutionAction { get; private set; }

    /// <summary>Signal (the waiting workflow continues), Repair (a repair workflow runs the effects), or RecordOnly.</summary>
    public string? ResolutionPath { get; private set; }

    public Guid? ResolutionId { get; private set; }

    public string? ResolutionEvidence { get; private set; }

    public string? ResolvedBy { get; private set; }

    public DateTime? ResolvedAt { get; private set; }

    /// <summary>Outcome reported by the repair workflow (Repair path only).</summary>
    public string? RepairOutcome { get; private set; }
}
