using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentsModule.Models;
using PaymentsModule.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace PaymentsModule.Tests;

public sealed class OrdersTopicIntegrationTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddLogging()
        .AddOptions()
        .AddDistributedMemoryCache()
        .AddSingleton<IPendingSessionIndex, InMemoryPendingSessionIndex>()
        .AddScoped<IValidator<OrdersBillRequestedEvent>, OrdersBillRequestedEventValidator>()
        .AddScoped<IValidator<OrdersOrderCancelledEvent>, OrdersOrderCancelledEventValidator>()
        .AddScoped<PendingBillService>()
        .AddSingleton<OrdersMessageHandler>()
        .BuildServiceProvider(true);

    private OrdersMessageHandler Handler => _provider.GetRequiredService<OrdersMessageHandler>();

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _provider.DisposeAsync();
    }

    [Theory]
    [InlineData("ordenes.cuenta.solicitada")]
    [InlineData("ordenes.cobro.solicitado")]
    public async Task Handles_orders_bill_requested_raw_json_and_persists_pending_bill(string routingKey)
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

        var outcome = await Handler.HandleAsync(routingKey, Encoding.UTF8.GetBytes(rawJson));
        Assert.Equal(OrdersMessageOutcome.Ack, outcome);

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
            Items: [new OrdersBillItem(null, "OFFER-TACO-01", "Tacos", 3, 60m, null, ["sin cebolla"], null)],
            Timestamp: default,
            IsTakeaway: true,
            WaiterName: " Ana ",
            OrderFolio: " ORD-TAKE-1 ");

        var outcome = await Handler.HandleAsync("ordenes.cuenta.solicitada", JsonSerializer.SerializeToUtf8Bytes(message));
        Assert.Equal(OrdersMessageOutcome.Ack, outcome);

        var saved = await GetBillAsync(orderId);
        Assert.NotNull(saved);
        Assert.Equal(orderId, saved.DinningSessionId);
        Assert.Equal(orderId, saved.OrderId);
        Assert.True(saved.IsTakeaway);
        Assert.Equal("Ana", saved.WaiterName);
        Assert.Equal("ORD-TAKE-1", saved.OrderFolio);
        Assert.Equal(180m, Assert.Single(saved.Items).Amount);
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
    public void Maps_guid_table_and_plate_ids_and_optional_notes()
    {
        var tableId = Guid.NewGuid();
        var plateId = Guid.NewGuid();
        var message = new OrdersBillRequestedEvent(
            EventId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            TableId: tableId.ToString(),
            WaiterId: "not-a-guid",
            Status: "WASTED",
            TotalAmount: 90m,
            Items: [new OrdersBillItem(null, plateId.ToString(), "Sopa", 1, 90m, 90m, null, null)],
            Timestamp: DateTimeOffset.UtcNow);

        var mapped = message.ToDinningSessionReadyEvent();
        Assert.Equal(tableId, mapped.TableId);
        Assert.Equal(plateId, Assert.Single(mapped.Items).DishId);
        Assert.Null(Assert.Single(mapped.Items).Notes);
        Assert.NotEqual(Guid.Empty, mapped.WaiterId);
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

        Assert.Equal(OrdersMessageOutcome.Ack, await Handler.HandleAsync("ordenes.cuenta.solicitada", JsonSerializer.SerializeToUtf8Bytes(bill)));
        Assert.NotNull(await GetBillAsync(sessionId));

        var cancel = new OrdersOrderCancelledEvent(
            EventId: Guid.NewGuid(),
            OrderId: orderId,
            SessionId: sessionId,
            Reason: "Cancelada por cliente",
            Timestamp: DateTimeOffset.UtcNow);

        Assert.Equal(OrdersMessageOutcome.Ack, await Handler.HandleAsync("ordenes.orden.cancelada", JsonSerializer.SerializeToUtf8Bytes(cancel)));
        Assert.Null(await GetBillAsync(sessionId));

        // Second cancellation on non-existent session stores tombstone cleanly.
        Assert.Equal(OrdersMessageOutcome.Ack, await Handler.HandleAsync("ordenes.order.cancelled", JsonSerializer.SerializeToUtf8Bytes(cancel)));

        // Late bill request for the cancelled session must be discarded by tombstone.
        var lateBill = bill with { EventId = Guid.NewGuid() };
        Assert.Equal(OrdersMessageOutcome.Ack, await Handler.HandleAsync("ordenes.cuenta.solicitada", JsonSerializer.SerializeToUtf8Bytes(lateBill)));
        Assert.Null(await GetBillAsync(sessionId));
    }

    [Fact]
    public async Task Handler_covers_unmapped_keys_null_payloads_and_invalid_cancellations()
    {
        Assert.Equal(OrdersMessageOutcome.Ack, await Handler.HandleAsync("ordenes.desconocida", "{}"u8.ToArray()));
        Assert.Equal(OrdersMessageOutcome.Reject, await Handler.HandleAsync("ordenes.cuenta.solicitada", "{bad json"u8.ToArray()));
        Assert.Equal(OrdersMessageOutcome.Reject, await Handler.HandleAsync("ordenes.cuenta.solicitada", "null"u8.ToArray()));
        Assert.Equal(OrdersMessageOutcome.Reject, await Handler.HandleAsync("ordenes.orden.cancelada", "{bad json"u8.ToArray()));
        Assert.Equal(OrdersMessageOutcome.Reject, await Handler.HandleAsync("ordenes.orden.cancelada", "null"u8.ToArray()));
        Assert.Equal(OrdersMessageOutcome.Reject, await Handler.HandleAsync("ordenes.orden.cancelada", "{}"u8.ToArray()));

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

        Assert.Equal(OrdersMessageOutcome.Reject, await Handler.HandleAsync("ordenes.cuenta.solicitada", JsonSerializer.SerializeToUtf8Bytes(invalid)));

        // EffectiveSessionId fallbacks: DinningSessionId and OrderId.
        var dinningOnly = new OrdersOrderCancelledEvent(DinningSessionId: Guid.NewGuid());
        Assert.Equal(dinningOnly.DinningSessionId, dinningOnly.EffectiveSessionId);

        var orderOnly = new OrdersOrderCancelledEvent(OrderId: Guid.NewGuid());
        Assert.Equal(orderOnly.OrderId, orderOnly.EffectiveSessionId);
        Assert.Equal(OrdersMessageOutcome.Ack, await Handler.HandleAsync("ordenes.orden.cancelada", JsonSerializer.SerializeToUtf8Bytes(orderOnly)));
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
        var bytes = payload.ToUtf8JsonBytes();

        using var doc = JsonDocument.Parse(bytes);
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

    [Fact]
    public async Task RabbitMq_connection_manager_validates_config_reuses_open_connection_and_reconnects()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => new RabbitMqConnectionManager(emptyConfig));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@localhost:5672" })
            .Build();

        var createdConnections = new List<FakeConnectionProxy>();
        await using var manager = new RabbitMqConnectionManager(config, (_, _) =>
        {
            var (conn, proxy) = FakeConnectionProxy.Create();
            createdConnections.Add(proxy);
            return Task.FromResult(conn);
        });

        var first = await manager.GetConnectionAsync();
        var second = await manager.GetConnectionAsync();
        Assert.Same(first, second);
        Assert.Single(createdConnections);

        // Simulate closed connection -> manager disposes old connection and reconnects.
        createdConnections[0].IsOpen = false;
        var reconnected = await manager.GetConnectionAsync();
        Assert.NotSame(first, reconnected);
        Assert.Equal(2, createdConnections.Count);
        Assert.True(createdConnections[0].Disposed);
    }

    [Fact]
    public async Task RabbitMq_publisher_sends_plain_json_to_ordenes_topic_and_noop_completes()
    {
        var (channel, channelProxy) = FakeChannelProxy.Create();
        var (conn, _) = FakeConnectionProxy.Create(channel);
        var manager = new StubConnectionManager(conn);
        var options = Options.Create(new OrdersTopicOptions());

        var publisher = new RabbitMqOrdersEventPublisher(manager, options, NullLogger<RabbitMqOrdersEventPublisher>.Instance);
        var evt = new OrdersPaymentCompletedEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PAID",
            250m,
            250m,
            "CARD",
            DateTimeOffset.UtcNow);

        await publisher.PublishPaymentCompletedAsync(evt);

        var published = Assert.Single(channelProxy.Published);
        Assert.Equal("ordenes.topic", published.Exchange);
        Assert.Equal("pagos.pago.completado", published.RoutingKey);
        Assert.Equal("application/json", published.Properties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, published.Properties.DeliveryMode);
        Assert.Equal(evt.EventId.ToString(), published.Properties.MessageId);

        using var doc = JsonDocument.Parse(published.Body);
        Assert.Equal(evt.OrderId, doc.RootElement.GetProperty("orderId").GetGuid());
        Assert.Equal(250m, doc.RootElement.GetProperty("totalPaid").GetDecimal());

        await new NoOpOrdersEventPublisher().PublishPaymentCompletedAsync(evt);
    }

    [Fact]
    public async Task RabbitMq_consumer_binds_single_queue_to_ordenes_topic_and_acks_or_nacks_deliveries()
    {
        var (channel, channelProxy) = FakeChannelProxy.Create();
        var (conn, _) = FakeConnectionProxy.Create(channel);
        var manager = new StubConnectionManager(conn);
        var options = Options.Create(new OrdersTopicOptions());

        var consumerService = new RabbitMqOrdersConsumerService(
            manager,
            Handler,
            options,
            NullLogger<RabbitMqOrdersConsumerService>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };

        await consumerService.StartAsync(CancellationToken.None);

        // Wait until BasicConsumeAsync has registered the consumer.
        for (var i = 0; i < 50 && channelProxy.RegisteredConsumer is null; i++)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(channelProxy.RegisteredConsumer);
        Assert.Empty(channelProxy.DeclaredExchanges);
        Assert.Contains("cola_payments_events", channelProxy.DeclaredQueues);
        Assert.Equal(4, channelProxy.Bindings.Count);
        Assert.All(channelProxy.Bindings, b => Assert.Equal(("cola_payments_events", "ordenes.topic"), (b.Queue, b.Exchange)));

        var sessionId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var validBill = new OrdersBillRequestedEvent(
            Guid.NewGuid(), orderId, sessionId, "MESA-9", Guid.NewGuid().ToString(), "DELIVERED", 99m,
            [new OrdersBillItem(Guid.NewGuid().ToString(), "P-9", "Café", 1, 99m, 99m)],
            DateTimeOffset.UtcNow);

        await channelProxy.RegisteredConsumer!.HandleBasicDeliverAsync(
            "ctag-1", 10, false, "ordenes.topic", "ordenes.cuenta.solicitada", new BasicProperties(),
            JsonSerializer.SerializeToUtf8Bytes(validBill));

        Assert.Contains(10UL, channelProxy.AckedTags);
        Assert.NotNull(await GetBillAsync(sessionId));

        await channelProxy.RegisteredConsumer.HandleBasicDeliverAsync(
            "ctag-1", 11, false, "ordenes.topic", "ordenes.cuenta.solicitada", new BasicProperties(),
            "{invalid"u8.ToArray());

        Assert.Contains((11UL, false), channelProxy.NackedTags);

        await consumerService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RabbitMq_consumer_creates_missing_topic_exchange_and_retries_transient_failures()
    {
        var (channel, channelProxy) = FakeChannelProxy.Create(failPassiveDeclare: true);
        var (conn, _) = FakeConnectionProxy.Create(channel);
        var manager = new StubConnectionManager(conn);
        var options = Options.Create(new OrdersTopicOptions());

        // Provider with a failing Redis cache to exercise the 3-attempt transient retry + final Nack.
        await using var failingProvider = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IDistributedCache, AlwaysFailingCache>()
            .AddSingleton<IPendingSessionIndex, InMemoryPendingSessionIndex>()
            .AddScoped<IValidator<OrdersBillRequestedEvent>, OrdersBillRequestedEventValidator>()
            .AddScoped<IValidator<OrdersOrderCancelledEvent>, OrdersOrderCancelledEventValidator>()
            .AddScoped<PendingBillService>()
            .AddSingleton<OrdersMessageHandler>()
            .BuildServiceProvider(true);

        var consumerService = new RabbitMqOrdersConsumerService(
            manager,
            failingProvider.GetRequiredService<OrdersMessageHandler>(),
            options,
            NullLogger<RabbitMqOrdersConsumerService>.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(1)
        };

        await consumerService.StartAsync(CancellationToken.None);
        for (var i = 0; i < 50 && channelProxy.RegisteredConsumer is null; i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains(("ordenes.topic", ExchangeType.Topic), channelProxy.DeclaredExchanges);

        var validBill = new OrdersBillRequestedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "MESA-2", null, "DELIVERED", 50m,
            [new OrdersBillItem(null, "P-2", "Té", 1, 50m, 50m)],
            DateTimeOffset.UtcNow);

        await channelProxy.RegisteredConsumer!.HandleBasicDeliverAsync(
            "ctag-1", 20, false, "ordenes.topic", "ordenes.cuenta.solicitada", new BasicProperties(),
            JsonSerializer.SerializeToUtf8Bytes(validBill));

        Assert.Contains((20UL, false), channelProxy.NackedTags);

        await consumerService.StopAsync(CancellationToken.None);
    }

    private async Task<DinningSessionReadyEvent?> GetBillAsync(Guid session)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PendingBillService>().GetAsync(session);
    }

    private sealed class StubConnectionManager(IConnection connection) : IRabbitMqConnectionManager
    {
        public Task<IConnection> GetConnectionAsync(CancellationToken ct = default) => Task.FromResult(connection);
    }

    private sealed class AlwaysFailingCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException("Redis down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis down");
        public void Refresh(string key) => throw new TimeoutException("Redis down");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis down");
        public void Remove(string key) => throw new TimeoutException("Redis down");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new TimeoutException("Redis down");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new TimeoutException("Redis down");
    }

    public class FakeConnectionProxy : DispatchProxy
    {
        private IChannel? _channel;

        public bool IsOpen { get; set; } = true;
        public bool Disposed { get; private set; }

        public static (IConnection Connection, FakeConnectionProxy Proxy) Create(IChannel? channel = null)
        {
            var conn = Create<IConnection, FakeConnectionProxy>();
            var proxy = (FakeConnectionProxy)(object)conn;
            proxy._channel = channel;
            return (conn, proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_IsOpen" => IsOpen,
            nameof(IConnection.CreateChannelAsync) => Task.FromResult(_channel ?? FakeChannelProxy.Create().Channel),
            nameof(IAsyncDisposable.DisposeAsync) => MarkDisposed(),
            _ => throw new NotSupportedException($"Unexpected IConnection call: {targetMethod?.Name}")
        };

        private ValueTask MarkDisposed()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public class FakeChannelProxy : DispatchProxy
    {
        private bool _failPassiveDeclare;

        public List<(string Exchange, string Type)> DeclaredExchanges { get; } = [];
        public List<string> DeclaredQueues { get; } = [];
        public List<(string Queue, string Exchange, string RoutingKey)> Bindings { get; } = [];
        public List<ulong> AckedTags { get; } = [];
        public List<(ulong Tag, bool Requeue)> NackedTags { get; } = [];
        public List<(string Exchange, string RoutingKey, IReadOnlyBasicProperties Properties, byte[] Body)> Published { get; } = [];
        public IAsyncBasicConsumer? RegisteredConsumer { get; private set; }

        public static (IChannel Channel, FakeChannelProxy Proxy) Create(bool failPassiveDeclare = false)
        {
            var ch = Create<IChannel, FakeChannelProxy>();
            var proxy = (FakeChannelProxy)(object)ch;
            proxy._failPassiveDeclare = failPassiveDeclare;
            return (ch, proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IChannel.ExchangeDeclarePassiveAsync):
                    if (_failPassiveDeclare)
                    {
                        return Task.FromException(new OperationInterruptedException(
                            new ShutdownEventArgs(ShutdownInitiator.Peer, 404, "NOT_FOUND")));
                    }

                    return Task.CompletedTask;

                case nameof(IChannel.ExchangeDeclareAsync):
                    DeclaredExchanges.Add(((string)args![0]!, (string)args[1]!));
                    return Task.CompletedTask;

                case nameof(IChannel.BasicQosAsync):
                    return Task.CompletedTask;

                case nameof(IChannel.QueueDeclareAsync):
                    var queue = (string)args![0]!;
                    DeclaredQueues.Add(queue);
                    return Task.FromResult(new QueueDeclareOk(queue, 0, 0));

                case nameof(IChannel.QueueBindAsync):
                    Bindings.Add(((string)args![0]!, (string)args[1]!, (string)args[2]!));
                    return Task.CompletedTask;

                case nameof(IChannel.BasicConsumeAsync):
                    RegisteredConsumer = args!.OfType<IAsyncBasicConsumer>().Single();
                    return Task.FromResult("ctag-1");

                case nameof(IChannel.BasicAckAsync):
                    AckedTags.Add((ulong)args![0]!);
                    return ValueTask.CompletedTask;

                case nameof(IChannel.BasicNackAsync):
                    NackedTags.Add(((ulong)args![0]!, (bool)args[2]!));
                    return ValueTask.CompletedTask;

                case nameof(IChannel.BasicPublishAsync):
                    var exchange = (string)args![0]!;
                    var routingKey = (string)args[1]!;
                    var props = args.OfType<IReadOnlyBasicProperties>().Single();
                    var body = args.OfType<ReadOnlyMemory<byte>>().Single().ToArray();
                    Published.Add((exchange, routingKey, props, body));
                    return ValueTask.CompletedTask;

                case nameof(IAsyncDisposable.DisposeAsync):
                    return ValueTask.CompletedTask;

                default:
                    throw new NotSupportedException($"Unexpected IChannel call: {targetMethod?.Name}");
            }
        }
    }
}
