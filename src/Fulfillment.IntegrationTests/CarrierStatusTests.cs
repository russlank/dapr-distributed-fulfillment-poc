extern alias shipping;
using System.Net;
using System.Text;
using System.Text.Json;
using Fulfillment.Contracts.Shipping;
using Fulfillment.Contracts.Workflows;
using Fulfillment.IntegrationTests.Infrastructure;
using Fulfillment.Messaging;
using Fulfillment.OrderApi.Carrier;
using Fulfillment.OrderApi.Orders;
using Fulfillment.ServiceDefaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using shipping::Fulfillment.ShippingService.Shipping;
using Xunit;

namespace Fulfillment.IntegrationTests;

/// <summary>
/// The carrier status path against SQL Server: Shipping's ingest with the current-state rule (every valid unique event
/// kept with its outcome; the status moves forward by version only; a terminal status never changes), the order API's
/// ingress (inbox and forward command, duplicates, payload conflicts), and the forwarder's decisions per Shipping
/// outcome (a signal only for a terminal status while the order awaits delivery, a case for a conflicting terminal
/// status, retries then a case for an unknown shipment).
/// </summary>
public sealed class CarrierStatusTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string shippingDb = "";
    private string orderDb = "";
    private ShippingOperations shippingOps = null!;
    private OrderApiHost api = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        shippingDb = await sql.CreateShippingDbAsync("ship");
        orderDb = await sql.CreateOrderDbAsync("carrier");
        shippingOps = new ShippingOperations(shippingDb, new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9") }, new ShippingOptions(), "shipping-test",
            NullLogger<ShippingOperations>.Instance);
        api = new OrderApiHost(orderDb);
        client = api.CreateCustomerClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await api.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> ShipmentAsync()
    {
        var order = Guid.NewGuid();
        await Db.ExecuteAsync(shippingDb,
            "INSERT shipping.Shipment (ShipmentId, OrderId, Parcels, State, CurrentVersion) VALUES (@s, @s, 1, 'Dispatched', 0)", ("@s", order));
        return order;
    }

    private Task<CarrierStatusIngested> IngestAsync(Guid shipment, string status, int version, Guid? eventId = null, Guid? orderId = null, string? hash = null)
    {
        var id = eventId ?? Guid.NewGuid();
        var at = new DateTime(2026, 11, 20, 10, 0, version, DateTimeKind.Utc);
        return shippingOps.IngestAsync(new CarrierStatusIngest(id, shipment, orderId ?? shipment, status, version, at, "callback",
            hash ?? CarrierEventHash.Of(id, shipment, orderId ?? shipment, status, version, at)), Ct);
    }

    [Fact]
    public async Task Ingest_OutOfOrderDuplicateAndLateConflict_KeepsEveryEventAndTheFirstTerminalStatus()
    {
        var shipment = await ShipmentAsync();
        var pickedUp = Guid.NewGuid();

        var results = new List<string>
        {
            (await IngestAsync(shipment, CarrierStatuses.PickedUp, 1, pickedUp)).Outcome,
            (await IngestAsync(shipment, CarrierStatuses.PickedUp, 1, pickedUp)).Outcome,
            (await IngestAsync(shipment, CarrierStatuses.Delivered, 3)).Outcome,
            (await IngestAsync(shipment, CarrierStatuses.InTransit, 2)).Outcome,
            (await IngestAsync(shipment, CarrierStatuses.Exception, 4)).Outcome,
            (await IngestAsync(shipment, CarrierStatuses.Delivered, 5)).Outcome,
        };

        Assert.Equal(
            [CarrierStatusOutcomes.Applied, CarrierStatusOutcomes.Applied, CarrierStatusOutcomes.TerminalApplied, CarrierStatusOutcomes.Stale,
                CarrierStatusOutcomes.TerminalConflict, CarrierStatusOutcomes.TerminalRepeat],
            results);
        Assert.Equal("Delivered|Delivered|3", await Db.ScalarAsync<string>(shippingDb,
            "SELECT CONCAT(TerminalStatus, '|', CurrentStatus, '|', CurrentVersion) FROM shipping.Shipment WHERE ShipmentId = @s", ("@s", shipment)));
        Assert.Equal(5, await Db.ScalarAsync<int>(shippingDb, "SELECT COUNT(*) FROM shipping.CarrierStatusEvent WHERE ShipmentId = @s", ("@s", shipment)));
        Assert.Equal(1, await Db.ScalarAsync<int>(shippingDb, "SELECT Replays FROM shipping.CarrierStatusEvent WHERE EventId = @e", ("@e", pickedUp)));
    }

    [Fact]
    public async Task Ingest_SameEventOtherPayload_IsAPayloadConflict_UnknownShipment_AndWrongOrder_ChangeNothing()
    {
        var shipment = await ShipmentAsync();
        var eventId = Guid.NewGuid();
        await IngestAsync(shipment, CarrierStatuses.PickedUp, 1, eventId);

        var conflict = await IngestAsync(shipment, CarrierStatuses.PickedUp, 1, eventId, hash: new string('a', 64));
        var unknown = await IngestAsync(Guid.NewGuid(), CarrierStatuses.PickedUp, 1);
        var wrongOrder = await IngestAsync(shipment, CarrierStatuses.InTransit, 2, orderId: Guid.NewGuid());
        var real = await IngestAsync(shipment, CarrierStatuses.InTransit, 2);

        Assert.Equal(
            (CarrierStatusOutcomes.PayloadConflict, CarrierStatusOutcomes.UnknownShipment, CarrierStatusOutcomes.CorrelationMismatch, CarrierStatusOutcomes.Applied),
            (conflict.Outcome, unknown.Outcome, wrongOrder.Outcome, real.Outcome));
        Assert.Equal("InTransit|2", await Db.ScalarAsync<string>(shippingDb,
            "SELECT CONCAT(CurrentStatus, '|', CurrentVersion) FROM shipping.Shipment WHERE ShipmentId = @s", ("@s", shipment)));
    }

    private static CarrierCallback Callback(Guid order, string status, int version, Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), order, order, status, version, new DateTime(2026, 11, 20, 10, 0, version, DateTimeKind.Utc));

    [Fact]
    public async Task Ingress_AcceptsOnceWithAForwardCommand_CountsDuplicates_AuditsAnotherPayload()
    {
        var order = Guid.NewGuid();
        var callback = Callback(order, CarrierStatuses.PickedUp, 1);

        var first = await CarrierIngress.AcceptAsync(orderDb, callback, Ct);
        var again = await CarrierIngress.AcceptAsync(orderDb, callback, Ct);
        var other = await CarrierIngress.AcceptAsync(orderDb, callback with { Version = 9 }, Ct);

        Assert.Equal(("accepted", "duplicate", "conflict"), (first, again, other));
        Assert.Equal("1|1|1|1", await Db.ScalarAsync<string>(orderDb,
            """
            SELECT CONCAT((SELECT COUNT(*) FROM orders.CarrierCallbackInbox WHERE EventId = @e), '|', (SELECT Duplicates FROM orders.CarrierCallbackInbox WHERE EventId = @e), '|',
              (SELECT COUNT(*) FROM orders.CarrierStatusForward WHERE EventId = @e AND State = 'Pending'), '|', (SELECT COUNT(*) FROM orders.CarrierCallbackConflict WHERE EventId = @e))
            """, ("@e", callback.EventId)));
    }

    /// <summary>A sidecar stand-in that answers every invocation with Shipping's given outcome.</summary>
    private sealed class ShippingAnswers(Func<CarrierStatusIngest, string> outcome) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var ingest = JsonSerializer.Deserialize<CarrierStatusIngest>(await request.Content!.ReadAsStringAsync(cancellationToken), JsonSerializerOptions.Web)!;
            var body = JsonSerializer.Serialize(new CarrierStatusIngested(ingest.EventId, outcome(ingest), false, CarrierStatuses.Delivered), JsonSerializerOptions.Web);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private CarrierForwarder Forwarder(ShippingAnswers answers, int unknownRetries = 5) =>
        new(orderDb, new SidecarInvoker(new HttpClient(answers) { BaseAddress = new Uri("http://localhost:3500/") }),
            new OrderLifecycleSteps(orderDb, StepCheckpoints.None), new Failpoints(null, NullLogger<Failpoints>.Instance),
            Options.Create(new CarrierForwarderOptions { UnknownShipmentRetries = unknownRetries, MaxBackoff = TimeSpan.Zero }),
            new RuntimeIdentity("order-api", "forwarder-test", "0", null, null, null, null, "release", "absent", "Development"),
            NullLogger<CarrierForwarder>.Instance);

    private async Task<Guid> OrderAwaitingDeliveryAsync()
    {
        using var response = await Orders.SubmitAsync(client, Orders.NewKey(), Orders.Body(Orders.NewCustomer("carrier"), ("SKU-1", 1)), Ct);
        var orderId = (await Orders.AcceptedAsync(response)).OrderId;
        await Fulfilling.EnterFulfillmentAsync(Fulfilling.Steps(orderDb), orderId, Ct);
        var lifecycle = new OrderLifecycleSteps(orderDb, StepCheckpoints.None);
        foreach (var to in new[] { "PaymentAuthorized", "Packing", "Dispatched", "AwaitingDelivery" })
        {
            await lifecycle.AdvanceAsync(new AdvanceFulfillmentRequest(orderId, to), Ct);
        }

        return orderId;
    }

    private async Task DrainAsync(CarrierForwarder forwarder)
    {
        for (var i = 0; i < 20 && await Db.ScalarAsync<int>(orderDb, "SELECT COUNT(*) FROM orders.CarrierStatusForward WHERE State = 'Pending'") > 0; i++)
        {
            await Db.ExecuteAsync(orderDb, "UPDATE orders.CarrierStatusForward SET NextAttemptAt = SYSUTCDATETIME() WHERE State = 'Pending'");
            await forwarder.RunOnceAsync(Ct);
        }
    }

    [Fact]
    public async Task Forward_TerminalAppliedWhileAwaitingDelivery_SignalsTheWorkflowOnce()
    {
        var order = await OrderAwaitingDeliveryAsync();
        var delivered = Callback(order, CarrierStatuses.Delivered, 3);
        await CarrierIngress.AcceptAsync(orderDb, delivered, Ct);
        var picked = Callback(order, CarrierStatuses.PickedUp, 1);
        await CarrierIngress.AcceptAsync(orderDb, picked, Ct);

        await DrainAsync(Forwarder(new ShippingAnswers(i => i.Status == CarrierStatuses.Delivered ? CarrierStatusOutcomes.TerminalApplied : CarrierStatusOutcomes.Applied)));

        Assert.Equal(CarrierSignals.EventName(order), await Db.ScalarAsync<string>(orderDb, "SELECT EventName FROM orders.PendingSignal WHERE OrderId = @o", ("@o", order)));
        Assert.Equal(1, await Db.ScalarAsync<int>(orderDb, "SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o", ("@o", order)));
        Assert.Equal("Applied,TerminalApplied", await Db.ScalarAsync<string>(orderDb,
            "SELECT STRING_AGG(Outcome, ',') WITHIN GROUP (ORDER BY Outcome) FROM orders.CarrierStatusForward WHERE EventId IN (@a, @b)", ("@a", delivered.EventId), ("@b", picked.EventId)));
    }

    [Fact]
    public async Task Forward_TerminalConflictOnACompletedOrder_OneCaseAndHistoryRow_NoSignal_StatusStays()
    {
        var order = await OrderAwaitingDeliveryAsync();
        await new OrderLifecycleSteps(orderDb, StepCheckpoints.None).FinalizeDeliveredAsync(new CompleteFulfillmentRequest(order, "fulfill-x"), Ct);
        var late = Callback(order, CarrierStatuses.Exception, 4);
        await CarrierIngress.AcceptAsync(orderDb, late, Ct);

        await DrainAsync(Forwarder(new ShippingAnswers(_ => CarrierStatusOutcomes.TerminalConflict)));

        Assert.Equal("Completed|1|0|CarrierTerminalConflict", await Db.ScalarAsync<string>(orderDb,
            """
            SELECT CONCAT((SELECT Status FROM orders.[Order] WHERE OrderId = @o), '|', (SELECT COUNT(*) FROM orders.InterventionCase WHERE OrderId = @o), '|',
              (SELECT COUNT(*) FROM orders.PendingSignal WHERE OrderId = @o), '|', (SELECT Kind FROM orders.InterventionCase WHERE OrderId = @o))
            """, ("@o", order)));
        Assert.Equal(1, await Db.ScalarAsync<int>(orderDb, "SELECT COUNT(*) FROM orders.OrderOutbox WHERE OrderId = @o AND EventType = 'OrderCompleted'", ("@o", order)));
        Assert.Equal("PostTerminalCase", await Db.ScalarAsync<string>(orderDb,
            "SELECT TOP (1) ReasonCode FROM orders.OrderStatusHistory WHERE OrderId = @o ORDER BY Sequence DESC", ("@o", order)));
    }

    [Fact]
    public async Task Forward_UnknownShipment_RetriedThenAnAmbiguousDispatchCase()
    {
        var order = await OrderAwaitingDeliveryAsync();
        var early = Callback(order, CarrierStatuses.PickedUp, 1);
        await CarrierIngress.AcceptAsync(orderDb, early, Ct);
        var answers = new ShippingAnswers(_ => CarrierStatusOutcomes.UnknownShipment);

        await DrainAsync(Forwarder(answers, unknownRetries: 2));

        Assert.Equal(3, answers.Calls);
        Assert.Equal("UnknownShipment|AmbiguousDispatch", await Db.ScalarAsync<string>(orderDb,
            "SELECT CONCAT((SELECT Outcome FROM orders.CarrierStatusForward WHERE EventId = @e), '|', (SELECT Kind FROM orders.InterventionCase WHERE OrderId = @o))",
            ("@e", early.EventId), ("@o", order)));
    }
}
