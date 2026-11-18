using Fulfillment.Contracts;
using Fulfillment.Contracts.Diagnostics;
using Fulfillment.WorkflowHost;
using Xunit;

namespace Fulfillment.UnitTests;

public class WorkflowManagementPolicyTests
{
    private static readonly WorkflowManagementPolicy Policy = WorkflowManagementPolicy.Default;

    [Fact]
    public void CanStart_AuthorizedCallerAndSupportedType_IsAllowed()
    {
        Assert.Equal(ManagementDecision.Allowed, Policy.CanStart(AppIds.OrderApi, RuntimeProbe.WorkflowName, "probe-1", replaceTerminal: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("compute-worker")]
    [InlineData("Order-Api")]
    public void CanStart_UnknownOrMissingCaller_IsRejected(string? caller)
    {
        Assert.Equal(ManagementDecision.CallerNotAuthorized, Policy.CanStart(caller, RuntimeProbe.WorkflowName, "probe-1", replaceTerminal: false));
    }

    [Fact]
    public void CanStart_UnsupportedWorkflowType_IsRejected()
    {
        Assert.Equal(ManagementDecision.WorkflowTypeNotSupported, Policy.CanStart(AppIds.OrderApi, "SomeOtherWorkflow", "probe-1", replaceTerminal: false));
    }

    [Fact]
    public void CanStart_ReplacementOfProbeInstance_IsAllowed()
    {
        Assert.Equal(ManagementDecision.Allowed, Policy.CanStart(AppIds.OrderApi, RuntimeProbe.WorkflowName, "probe-1", replaceTerminal: true));
    }

    [Fact]
    public void CanStart_ReplacementOfNonProbeInstance_IsRejected()
    {
        Assert.Equal(ManagementDecision.ReplacementNotAllowed, Policy.CanStart(AppIds.OrderApi, RuntimeProbe.WorkflowName, "order-1", replaceTerminal: true));
    }

    [Fact]
    public void CanStart_ReplacementOfOtherSupportedType_IsRejected()
    {
        var policy = new WorkflowManagementPolicy([AppIds.OrderApi], [RuntimeProbe.WorkflowName, "FulfillmentWorkflow"]);

        Assert.Equal(ManagementDecision.ReplacementNotAllowed, policy.CanStart(AppIds.OrderApi, "FulfillmentWorkflow", "probe-1", replaceTerminal: true));
    }

    [Fact]
    public void CanAccess_ChecksCallerAndExistingType()
    {
        Assert.Equal(ManagementDecision.Allowed, Policy.CanAccess(AppIds.OrderApi, RuntimeProbe.WorkflowName));
        Assert.Equal(ManagementDecision.Allowed, Policy.CanAccess(AppIds.OrderApi, existingWorkflowName: null));
        Assert.Equal(ManagementDecision.CallerNotAuthorized, Policy.CanAccess("inventory-service", RuntimeProbe.WorkflowName));
        Assert.Equal(ManagementDecision.WorkflowTypeNotSupported, Policy.CanAccess(AppIds.OrderApi, "SomeOtherWorkflow"));
    }
}
