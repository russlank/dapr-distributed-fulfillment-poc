using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.OrderApi.Reviews;

// The review steps of the fulfillment workflow, hosted on order-api because they read and write
// OrderDb. Thin adapters over ReviewSteps; every one is safe to repeat.

public sealed class CreateReviewTaskActivity(ReviewSteps steps) : WorkflowActivity<CreateReviewRequest, ReviewRound>
{
    public override Task<ReviewRound> RunAsync(WorkflowActivityContext context, CreateReviewRequest input) => steps.CreateAsync(input);
}

public sealed class ResolveReviewActivity(ReviewSteps steps) : WorkflowActivity<ResolveReviewRequest, ReviewResolution>
{
    public override Task<ReviewResolution> RunAsync(WorkflowActivityContext context, ResolveReviewRequest input) => steps.ResolveAsync(input);
}

public sealed class ExpireOrEscalateReviewActivity(ReviewSteps steps) : WorkflowActivity<ExpireReviewRequest, ExpiryResult>
{
    public override Task<ExpiryResult> RunAsync(WorkflowActivityContext context, ExpireReviewRequest input) => steps.ExpireOrEscalateAsync(input);
}
