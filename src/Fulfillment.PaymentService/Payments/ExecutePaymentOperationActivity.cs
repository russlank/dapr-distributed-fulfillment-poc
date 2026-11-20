using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.PaymentService.Payments;

/// <summary>
/// One payment operation by its operation key. Safe to run any number of times, also concurrently: a finished
/// operation returns its stored outcome, an unfinished one reconciles with the provider before sending again.
/// </summary>
public sealed class ExecutePaymentOperationActivity(PaymentExecutor executor) : WorkflowActivity<PaymentOperationRequest, PaymentOperationResult>
{
    public override Task<PaymentOperationResult> RunAsync(WorkflowActivityContext context, PaymentOperationRequest input) =>
        executor.ExecuteAsync(input, CancellationToken.None);
}

/// <summary>Adopts the provider's record for an operation finished Ambiguous (after an intervention case); sends nothing.</summary>
public sealed class ReconcilePaymentOperationActivity(PaymentExecutor executor) : WorkflowActivity<PaymentOperationRequest, PaymentOperationResult>
{
    public override Task<PaymentOperationResult> RunAsync(WorkflowActivityContext context, PaymentOperationRequest input) =>
        executor.ReconcileFinishedAsync(input, CancellationToken.None);
}
