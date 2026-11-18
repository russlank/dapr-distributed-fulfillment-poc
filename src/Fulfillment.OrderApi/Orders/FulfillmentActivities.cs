using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;

namespace Fulfillment.OrderApi.Orders;

// The fulfillment workflow's OrderDb steps, hosted on order-api because they write OrderDb. Each is
// a thin adapter over FulfillmentSteps; repeated executions (retries, a second run) are safe there.

public sealed class ValidateFulfillmentOrderActivity(FulfillmentSteps steps) : WorkflowActivity<ValidateOrderRequest, OrderValidationResult>
{
    public override Task<OrderValidationResult> RunAsync(WorkflowActivityContext context, ValidateOrderRequest input) => steps.ValidateAsync(input);
}

public sealed class RecordFulfillmentChecksActivity(FulfillmentSteps steps) : WorkflowActivity<RecordChecksRequest, ChecksRecorded>
{
    public override Task<ChecksRecorded> RunAsync(WorkflowActivityContext context, RecordChecksRequest input) => steps.RecordChecksAsync(input);
}

public sealed class BeginStubFulfillmentActivity(FulfillmentSteps steps) : WorkflowActivity<BeginFulfillmentRequest, StepResult>
{
    public override Task<StepResult> RunAsync(WorkflowActivityContext context, BeginFulfillmentRequest input) => steps.BeginFulfillmentAsync(input);
}

public sealed class ApplyStubFulfillmentStepActivity(FulfillmentSteps steps) : WorkflowActivity<StubStepRequest, StepResult>
{
    public override Task<StepResult> RunAsync(WorkflowActivityContext context, StubStepRequest input) => steps.ApplyStubStepAsync(input);
}

public sealed class CompleteStubFulfillmentActivity(FulfillmentSteps steps) : WorkflowActivity<CompleteFulfillmentRequest, StepResult>
{
    public override Task<StepResult> RunAsync(WorkflowActivityContext context, CompleteFulfillmentRequest input) => steps.CompleteAsync(input);
}
