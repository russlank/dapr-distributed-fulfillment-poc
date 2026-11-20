using Dapr.Workflow;
using Fulfillment.Contracts.Workflows;
using Fulfillment.Primitives;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Fulfillment.InventoryService.Inventory;

/// <summary>Failpoint names of the inventory activities (drill builds only; key: the line's or the order's operation key).</summary>
public static class InventoryFailpoints
{
    /// <summary>After the reservation committed, before the activity returns to Dapr (key: {orderId:N}:reserve:line-N).</summary>
    public const string AfterBusinessCommitBeforeActivityCompletion = "AfterBusinessCommitBeforeActivityCompletion";

    /// <summary>After all holds were consumed, before the activity returns (key: {orderId:N}:consume:1).</summary>
    public const string AfterConsumeCommitBeforeActivityCompletion = "AfterConsumeCommitBeforeActivityCompletion";

    /// <summary>After one hold of a compensation was released, before the next one (key: {orderId:N}:release:{holdId:N}).</summary>
    public const string AfterReleaseOneHoldBeforeNext = "AfterReleaseOneHoldBeforeNext";
}

/// <summary>Operation keys of inventory requests: stable per order, line, and warehouse.</summary>
public static class InventoryKeys
{
    public static string ReserveLine(Guid orderId, int line) => OperationKey.For(orderId, "reserve", $"line-{line}").Value;

    public static string Reserve(Guid orderId, int line, string warehouseId) => OperationKey.For(orderId, "reserve", $"line-{line}-{warehouseId}").Value;

    public static string Release(Guid orderId, Guid holdId) => OperationKey.For(orderId, "release", holdId.ToString("N")).Value;

    public static string Close(Guid orderId) => OperationKey.For(orderId, "close", "1").Value;

    public static string Consume(Guid orderId) => OperationKey.For(orderId, "consume", "1").Value;

    public static string Extend(Guid orderId) => OperationKey.For(orderId, "extend", "1").Value;
}

/// <summary>
/// Whole-line allocation: warehouses in priority order, the whole quantity from the first one that
/// can serve it, no split. Each warehouse request has its own key, so a retry after a lost answer
/// replays every earlier answer in the same order and ends at the same warehouse.
/// </summary>
public sealed class ReserveOrderLineActivity(InventoryOperations operations, IStockCoordinator coordinator, Failpoints failpoints, IOptions<InventoryOptions> options)
    : WorkflowActivity<ReserveLineRequest, ReserveLineResult>
{
    public override async Task<ReserveLineResult> RunAsync(WorkflowActivityContext context, ReserveLineRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tried = new List<string>();
        foreach (var warehouse in await operations.WarehousesAsync())
        {
            var command = new ReserveCommand(input.OrderId, input.LineNumber, input.Sku, input.Quantity, warehouse,
                InventoryKeys.Reserve(input.OrderId, input.LineNumber, warehouse), options.Value.HoldTtl);
            var result = await coordinator.ReserveAsync(command, CancellationToken.None);
            tried.Add($"{warehouse}:{result.Outcome}{(result.Replayed ? "(replay)" : "")}");
            if (result.Outcome == InventoryOutcomes.Reserved)
            {
                await failpoints.HitAsync(InventoryFailpoints.AfterBusinessCommitBeforeActivityCompletion, InventoryKeys.ReserveLine(input.OrderId, input.LineNumber));
                return new ReserveLineResult(InventoryOutcomes.Reserved, result.HoldId, warehouse, result.ExpiresAtUtc, tried, operations.Replica);
            }

            if (result.Outcome == InventoryOutcomes.OrderClosed)
            {
                return new ReserveLineResult(InventoryOutcomes.OrderClosed, null, null, null, tried, operations.Replica);
            }
        }

        return new ReserveLineResult(InventoryOutcomes.InsufficientStock, null, null, null, tried, operations.Replica);
    }
}

/// <summary>Closes the order for reservations, then releases each hold still held (through its stock actor).</summary>
public sealed class ReleaseOrderHoldsActivity(InventoryOperations operations, IStockCoordinator coordinator, Failpoints failpoints)
    : WorkflowActivity<ReleaseHoldsRequest, ReleaseHoldsResult>
{
    public override async Task<ReleaseHoldsResult> RunAsync(WorkflowActivityContext context, ReleaseHoldsRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var held = await operations.CloseOrderAsync(input.OrderId, "Released", InventoryKeys.Close(input.OrderId));
        int released = 0, notHeld = 0;
        foreach (var hold in held)
        {
            var key = InventoryKeys.Release(input.OrderId, hold.HoldId);
            var result = await coordinator.ReleaseAsync(hold.WarehouseId, hold.Sku, new ReleaseCommand(input.OrderId, hold.HoldId, key), CancellationToken.None);
            if (result.Outcome == InventoryOutcomes.Released)
            {
                released++;
                await failpoints.HitAsync(InventoryFailpoints.AfterReleaseOneHoldBeforeNext, key);
            }
            else
            {
                notHeld++;
            }
        }

        return new ReleaseHoldsResult(released, notHeld, operations.Replica);
    }
}

/// <summary>Consumes all holds of the order atomically (straight in SQL: one transaction across stock rows).</summary>
public sealed class ConsumeOrderHoldsActivity(InventoryOperations operations, Failpoints failpoints) : WorkflowActivity<ConsumeHoldsRequest, ConsumeHoldsResult>
{
    public override async Task<ConsumeHoldsResult> RunAsync(WorkflowActivityContext context, ConsumeHoldsRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var key = InventoryKeys.Consume(input.OrderId);
        var result = await operations.ConsumeOrderAsync(input.OrderId, input.ExpectedLines, key);
        if (result.Outcome == InventoryOutcomes.Consumed)
        {
            await failpoints.HitAsync(InventoryFailpoints.AfterConsumeCommitBeforeActivityCompletion, key);
        }

        return new ConsumeHoldsResult(result.Outcome, result.Consumed, operations.Replica);
    }
}

/// <summary>Extends all holds of the order once before the payment capture (direct guarded SQL, like consumption).</summary>
public sealed class ExtendOrderHoldsActivity(InventoryOperations operations) : WorkflowActivity<ExtendHoldsRequest, ExtendHoldsResult>
{
    public override Task<ExtendHoldsResult> RunAsync(WorkflowActivityContext context, ExtendHoldsRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return operations.ExtendOrderAsync(input.OrderId, input.ExpectedLines, input.Extension, input.AuthorizationExpiresAtUtc,
            input.MinimumRemaining, InventoryKeys.Extend(input.OrderId));
    }
}
