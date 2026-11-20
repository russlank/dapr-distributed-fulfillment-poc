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

public sealed class CloseUnfulfilledOrderActivity(FulfillmentSteps steps) : WorkflowActivity<CloseUnfulfilledRequest, StepResult>
{
    public override Task<StepResult> RunAsync(WorkflowActivityContext context, CloseUnfulfilledRequest input) => steps.CloseUnfulfilledAsync(input);
}

public sealed class AdvanceFulfillmentActivity(OrderLifecycleSteps steps) : WorkflowActivity<AdvanceFulfillmentRequest, StepResult>
{
    public override Task<StepResult> RunAsync(WorkflowActivityContext context, AdvanceFulfillmentRequest input) => steps.AdvanceAsync(input);
}

public sealed class BeginDispatchActivity(OrderLifecycleSteps steps) : WorkflowActivity<BeginDispatchRequest, BeginDispatchResult>
{
    public override Task<BeginDispatchResult> RunAsync(WorkflowActivityContext context, BeginDispatchRequest input) => steps.BeginDispatchAsync(input);
}

public sealed class OpenInterventionCaseActivity(OrderLifecycleSteps steps) : WorkflowActivity<OpenCaseRequest, CaseResult>
{
    public override Task<CaseResult> RunAsync(WorkflowActivityContext context, OpenCaseRequest input) => steps.OpenCaseAsync(input);
}

public sealed class ReadInterventionCaseActivity(OrderLifecycleSteps steps) : WorkflowActivity<ReadCaseRequest, CaseResult>
{
    public override Task<CaseResult> RunAsync(WorkflowActivityContext context, ReadCaseRequest input) => steps.ReadCaseAsync(input);
}
