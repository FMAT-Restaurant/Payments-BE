using System.Text.Json;
using FluentValidation;
using MassTransit;
using MassTransit.Serialization;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Consumers;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

public class OrdersTopicIntegrationTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddLogging()
        .AddDistributedMemoryCache()
        .AddSingleton<IPendingSessionIndex, InMemoryPendingSessionIndex>()
        .AddScoped<IValidator<OrdersBillRequestedEvent>, OrdersBillRequestedEventValidator>()
        .AddScoped<IValidator<OrdersOrderCancelledEvent>, OrdersOrderCancelledEventValidator>()
        .AddScoped<PendingBillService>()
        .AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<OrdersBillRequestedConsumer, OrdersBillRequestedConsumerDefinition>();
            x.AddConsumer<OrdersOrderCancelledConsumer, OrdersOrderCancelledConsumerDefinition>();
        })
        .BuildServiceProvider(true);

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public Task InitializeAsync() => Harness.Start();

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Consumes_orders_bill_requested_raw_json_and_persists_pending_bill()
    {
        // Exact wire payload documented in OBS-02 section 4.1 (ordenes.cuenta.solicitada / ordenes.cobro.solicitado).
        const string rawJson = """
            {
              "eventId": "44444444-5555-4666-8777-888888888888",
              "orderId": "33333333-3333-3333-3333-333333333333",
              "sessionId": "22222222-3333-4444-8555-666666666666",
              "tableId": "MESA-7",
              "waiterId": "00000000-0000-0000-0000-000000000001",
              "status": "DELIVERED",
              "totalAmount": 250.0,
              "items": [
                {
                  "itemId": "33333333-0000-0000-0000-000000000001",
                  "plateId": "OFFER-PASTA-01",
                  "name": "Pasta Tartufo",
                  "quantity": 2,
                  "unitPrice": 125.0,
                  "subtotal": 250.0,
                  "modifiers": ["extra queso"],
                  "notes": "Servir caliente"
                }
              ],
              "timestamp": "2026-11-25T18:05:00Z"
            }
            """;

        var message = JsonSerializer.Deserialize<OrdersBillRequestedEvent>(rawJson)!;
        var fault = await PublishBillAsync(message);

        Assert.Null(fault);

        var sessionId = Guid.Parse("22222222-3333-4444-8555-666666666666");
        var orderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var saved = await GetBillAsync(sessionId);

        Assert.NotNull(saved);
        Assert.Equal(sessionId, saved.DinningSessionId);
        Assert.Equal(orderId, saved.OrderId);
        Assert.False(saved.IsTakeaway);
        Assert.Equal("MESA-7", saved.TableName);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), saved.WaiterId);
        Assert.Equal(250m, saved.Totals.ExpectedTotal);
        Assert.Equal(250m, saved.Totals.Subtotal);
        Assert.Equal(0m, saved.Totals.Taxes);

        var item = Assert.Single(saved.Items);
        Assert.Equal(Guid.Parse("33333333-0000-0000-0000-000000000001"), item.DishId);
        Assert.Equal("Pasta Tartufo", item.Name);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(125m, item.UnitPrice);
        Assert.Equal(250m, item.Amount);
        Assert.Equal("Servir caliente", item.Notes);
        Assert.True(((InMemoryPendingSessionIndex)_provider.GetRequiredService<IPendingSessionIndex>()).Contains(sessionId));
    }

    [Fact]
    public async Task Takeaway_order_with_null_session_id_uses_order_id_and_emits_null_session_on_payment()
    {
        var orderId = Guid.NewGuid();
        var message = new OrdersBillRequestedEvent(
            EventId: Guid.NewGuid(),
            OrderId: orderId,
            SessionId: null,
            TableId: null,
            WaiterId: null,
            Status: "DELIVERED",
            TotalAmount: 180m,
            Items: [new OrdersBillItem(null, "OFFER-TACO-01", "Tacos", 3, 60m, 180m, ["sin cebolla"], null)],
            Timestamp: DateTimeOffset.UtcNow,
            IsTakeaway: true);

        var fault = await PublishBillAsync(message);
        Assert.Null(fault);

        var saved = await GetBillAsync(orderId);
        Assert.NotNull(saved);
        Assert.Equal(orderId, saved.DinningSessionId);
        Assert.Equal(orderId, saved.OrderId);
        Assert.True(saved.IsTakeaway);
        Assert.Equal("sin cebolla", Assert.Single(saved.Items).Notes);

        var tx = new PaymentTransaction
        {
            Folio = "TX-20261125-000001",
            DinningSessionId = orderId,
            CashierId = "cashier-1",
            Total = 180m,
            CreatedAt = DateTimeOffset.Parse("2026-11-25T18:07:30Z"),
            Payments = [new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = 180m }]
        };

        var outgoing = OrdersPaymentCompletedEvent.FromCharge(saved, tx);
        Assert.Equal(orderId, outgoing.OrderId);
        Assert.Null(outgoing.SessionId);
        Assert.Equal("PAID", outgoing.Status);
        Assert.Equal(180m, outgoing.TotalPaid);
        Assert.Equal(180m, outgoing.MontoTotal);
        Assert.Equal("CASH", outgoing.PaymentMethod);
    }

    [Fact]
    public async Task Order_cancellation_removes_pending_bill_and_blocks_late_bill_events()
    {
        var orderId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var bill = new OrdersBillRequestedEvent(
            EventId: Guid.NewGuid(),
            OrderId: orderId,
            SessionId: sessionId,
            TableId: "MESA-3",
            WaiterId: Guid.NewGuid().ToString(),
            Status: "DELIVERED",
            TotalAmount: 120m,
            Items: [new OrdersBillItem(Guid.NewGuid().ToString(), "P-1", "Agua", 2, 60m, 120m)],
            Timestamp: DateTimeOffset.UtcNow);

        Assert.Null(await PublishBillAsync(bill));
        Assert.NotNull(await GetBillAsync(sessionId));

        var cancel = new OrdersOrderCancelledEvent(
            EventId: Guid.NewGuid(),
            OrderId: orderId,
            SessionId: sessionId,
            Reason: "Cancelada por cliente",
            Timestamp: DateTimeOffset.UtcNow);

        Assert.Null(await PublishCancelAsync(cancel));
        Assert.Null(await GetBillAsync(sessionId));

        // Late bill request for the cancelled session must be discarded by tombstone.
        Assert.Null(await PublishBillAsync(bill with { EventId = Guid.NewGuid() }));
        Assert.Null(await GetBillAsync(sessionId));
    }

    [Fact]
    public async Task Rejects_invalid_bill_requested_event_with_validation_exception()
    {
        var invalid = new OrdersBillRequestedEvent(
            EventId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            TableId: "MESA-1",
            WaiterId: null,
            Status: "DELIVERED",
            TotalAmount: 0m,
            Items: [],
            Timestamp: DateTimeOffset.UtcNow);

        var fault = await PublishBillAsync(invalid);
        Assert.IsType<ValidationException>(fault);
    }

    [Fact]
    public void Payment_completed_event_matches_obs02_raw_json_contract()
    {
        var orderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var sessionId = Guid.Parse("22222222-3333-4444-8555-666666666666");
        var paidAt = DateTimeOffset.Parse("2026-11-25T18:07:30Z");

        var bill = new DinningSessionReadyEvent(
            Guid.NewGuid(), sessionId, Guid.NewGuid(), Guid.NewGuid(), paidAt,
            [new BillItem(Guid.NewGuid(), "Pasta Tartufo", 2, 125m, 250m)],
            new BillTotals(250m, 0m, 250m),
            TableName: "MESA-7",
            OrderId: orderId,
            IsTakeaway: false);

        var cardTx = new PaymentTransaction
        {
            Folio = "TX-20261125-000001",
            DinningSessionId = sessionId,
            CashierId = "cashier-1",
            Total = 250m,
            CreatedAt = paidAt,
            Payments = [new PaymentBreakdown { Method = PaymentMethod.CreditCard, Amount = 250m, AuthorizationReference = "AUTH-1" }]
        };

        var payload = OrdersPaymentCompletedEvent.FromCharge(bill, cardTx);
        var serializer = OrdersPaymentCompletedEvent.RawJsonSerializer;
        var sendContext = new MassTransit.Context.MessageSendContext<OrdersPaymentCompletedEvent>(payload);
        var body = serializer.GetMessageBody(sendContext);
        var json = body.GetString();

        Assert.Equal("application/json", serializer.ContentType.MediaType);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(orderId, root.GetProperty("orderId").GetGuid());
        Assert.Equal(sessionId, root.GetProperty("sessionId").GetGuid());
        Assert.Equal("PAID", root.GetProperty("status").GetString());
        Assert.Equal(250m, root.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(250m, root.GetProperty("montoTotal").GetDecimal());
        Assert.Equal("CARD", root.GetProperty("paymentMethod").GetString());
        Assert.Equal(paidAt, root.GetProperty("paidAt").GetDateTimeOffset());
        Assert.False(root.TryGetProperty("messageType", out _));

        Assert.Equal("MIXED", OrdersPaymentCompletedEvent.ResolvePaymentMethod([
            new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = 100m },
            new PaymentBreakdown { Method = PaymentMethod.DebitCard, Amount = 150m }
        ]));
    }

    private async Task<Exception?> PublishBillAsync(OrdersBillRequestedEvent message)
    {
        var consumed = Harness.GetConsumerHarness<OrdersBillRequestedConsumer>().Consumed;
        IReceivedMessage<OrdersBillRequestedEvent>[] Received() =>
            consumed.Select<OrdersBillRequestedEvent>(m => m.Context.Message.EventId == message.EventId).ToArray();

        var before = Received().Length;
        await Harness.Bus.Publish(message);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Received().Length == before)
        {
            await Task.Delay(20, timeout.Token);
        }

        return Received().Last().Exception;
    }

    private async Task<Exception?> PublishCancelAsync(OrdersOrderCancelledEvent message)
    {
        var consumed = Harness.GetConsumerHarness<OrdersOrderCancelledConsumer>().Consumed;
        IReceivedMessage<OrdersOrderCancelledEvent>[] Received() =>
            consumed.Select<OrdersOrderCancelledEvent>(m => m.Context.Message.EffectiveSessionId == message.EffectiveSessionId).ToArray();

        var before = Received().Length;
        await Harness.Bus.Publish(message);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Received().Length == before)
        {
            await Task.Delay(20, timeout.Token);
        }

        return Received().Last().Exception;
    }

    private async Task<DinningSessionReadyEvent?> GetBillAsync(Guid session)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PendingBillService>().GetAsync(session);
    }
}
