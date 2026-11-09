using FluentValidation;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Consumers;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

public class DinningSessionCancelledConsumerTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddLogging()
        .AddDistributedMemoryCache()
        .AddSingleton<IPendingSessionIndex, InMemoryPendingSessionIndex>()
        .AddScoped<IValidator<DinningSessionCancelledEvent>, DinningSessionCancelledEventValidator>()
        .AddScoped<IValidator<DinningSessionReadyEvent>, DinningSessionReadyEventValidator>()
        .AddScoped<PendingBillService>()
        .AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<DinningSessionCancelledConsumer, DinningSessionCancelledConsumerDefinition>();
            x.AddConsumer<DinningSessionReadyConsumer, DinningSessionReadyConsumerDefinition>();
        })
        .BuildServiceProvider(true);

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    private PendingBillService GetBills() => _provider.CreateScope().ServiceProvider.GetRequiredService<PendingBillService>();

    public Task InitializeAsync() => Harness.Start();

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // Scenario 1: Eliminación exitosa de una cuenta pendiente
    [Fact]
    public async Task Existing_session_is_removed_from_cache_and_index_on_cancellation()
    {
        var session = Guid.NewGuid();
        var bills = GetBills();

        // Seed pending bill
        await bills.SaveSnapshotAsync(NewReadyEvent(session, 150m));
        Assert.NotNull(await bills.GetPendingBillAsync(session));
        var initialList = await bills.GetAllPendingAsync();
        Assert.Contains(initialList, b => b.DinningSessionId == session);

        // Publish cancellation event
        var cancelEvent = new DinningSessionCancelledEvent(
            Guid.NewGuid(), session, DateTimeOffset.UtcNow, "Cliente canceló orden");

        var fault = await PublishAsync(cancelEvent);

        Assert.Null(fault);
        Assert.Null(await bills.GetPendingBillAsync(session));

        var afterList = await bills.GetAllPendingAsync();
        Assert.DoesNotContain(afterList, b => b.DinningSessionId == session);
    }

    // Scenario 2: Manejo idempotente de un evento duplicado o cuenta inexistente
    [Fact]
    public async Task Non_existent_or_duplicate_cancellation_event_is_handled_idempotently_without_error()
    {
        var session = Guid.NewGuid();
        var cancelEvent = new DinningSessionCancelledEvent(
            Guid.NewGuid(), session, DateTimeOffset.UtcNow, "Mesa no existía");

        var fault = await PublishAsync(cancelEvent);

        Assert.Null(fault);

        // Second delivery (duplicate event) is also handled gracefully without error
        var secondFault = await PublishAsync(cancelEvent);
        Assert.Null(secondFault);
    }

    // Scenario 3: Fallo de infraestructura (Redis caído) -> lanza excepción para reintento/DLQ
    [Fact]
    public async Task Redis_failure_throws_exception_to_trigger_masstransit_retry_or_dlq()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IDistributedCache, ThrowingCache>()
            .AddSingleton<IPendingSessionIndex, InMemoryPendingSessionIndex>()
            .AddScoped<IValidator<DinningSessionCancelledEvent>, DinningSessionCancelledEventValidator>()
            .AddScoped<PendingBillService>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<DinningSessionCancelledConsumer, DinningSessionCancelledConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = services.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var session = Guid.NewGuid();
            var cancelEvent = new DinningSessionCancelledEvent(Guid.NewGuid(), session, DateTimeOffset.UtcNow);

            await harness.Bus.Publish(cancelEvent);

            var consumerHarness = harness.GetConsumerHarness<DinningSessionCancelledConsumer>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (!await consumerHarness.Consumed.Any<DinningSessionCancelledEvent>(m => m.Context.Message.EventId == cancelEvent.EventId)
                   && !timeout.IsCancellationRequested)
            {
                await Task.Delay(20, timeout.Token);
            }

            // Exception was thrown during consumer processing
            var consumedMessage = consumerHarness.Consumed.Select<DinningSessionCancelledEvent>().LastOrDefault();
            Assert.NotNull(consumedMessage?.Exception);
        }
        finally
        {
            await harness.Stop();
            await services.DisposeAsync();
        }
    }

    // Scenario 4: Out-of-Order (Anulación llega antes que creación)
    [Fact]
    public async Task Cancellation_before_creation_sets_tombstone_and_prevents_late_creation_snapshot()
    {
        var session = Guid.NewGuid();
        var bills = GetBills();

        // 1. Cancellation event arrives first
        var cancelEvent = new DinningSessionCancelledEvent(
            Guid.NewGuid(), session, DateTimeOffset.UtcNow, "Cancelado anticipadamente");
        var cancelFault = await PublishAsync(cancelEvent);
        Assert.Null(cancelFault);

        // 2. Late ready event arrives afterwards
        var readyEvent = NewReadyEvent(session, 200m);
        var readyFault = await PublishReadyAsync(readyEvent);
        Assert.Null(readyFault);

        // The session must NOT be in Redis or in the active index
        Assert.Null(await bills.GetPendingBillAsync(session));
        var list = await bills.GetAllPendingAsync();
        Assert.DoesNotContain(list, b => b.DinningSessionId == session);
    }

    // Validation
    [Fact]
    public async Task Event_with_empty_session_id_is_faulted()
    {
        var invalid = new DinningSessionCancelledEvent(Guid.Empty, Guid.Empty, DateTimeOffset.UtcNow);

        var fault = await PublishAsync(invalid);

        Assert.NotNull(fault);
    }

    [Fact]
    public async Task Event_without_event_id_is_accepted_and_valid()
    {
        var session = Guid.NewGuid();
        var cancelEvent = new DinningSessionCancelledEvent(Guid.Empty, session, DateTimeOffset.UtcNow);

        var fault = await PublishAsync(cancelEvent);

        Assert.Null(fault);
    }

    private async Task<Exception?> PublishAsync(DinningSessionCancelledEvent message)
    {
        var consumed = Harness.GetConsumerHarness<DinningSessionCancelledConsumer>().Consumed;
        IReceivedMessage<DinningSessionCancelledEvent>[] Received() =>
            consumed.Select<DinningSessionCancelledEvent>(m => m.Context.Message.DinningSessionId == message.DinningSessionId).ToArray();

        var before = Received().Length;
        await Harness.Bus.Publish(message);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Received().Length == before && !timeout.IsCancellationRequested)
        {
            await Task.Delay(20, timeout.Token);
        }

        return Received().LastOrDefault()?.Exception;
    }

    private async Task<Exception?> PublishReadyAsync(DinningSessionReadyEvent message)
    {
        var consumed = Harness.GetConsumerHarness<DinningSessionReadyConsumer>().Consumed;
        IReceivedMessage<DinningSessionReadyEvent>[] Received() =>
            consumed.Select<DinningSessionReadyEvent>(m => m.Context.Message.EventId == message.EventId).ToArray();

        var before = Received().Length;
        await Harness.Bus.Publish(message);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Received().Length == before && !timeout.IsCancellationRequested)
        {
            await Task.Delay(20, timeout.Token);
        }

        return Received().LastOrDefault()?.Exception;
    }

    private static DinningSessionReadyEvent NewReadyEvent(Guid session, decimal total) => new(
        Guid.NewGuid(),
        session,
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        [new BillItem(Guid.NewGuid(), "Tacos al Pastor", 2, total / 2, total)],
        new BillTotals(total * 0.86m, total * 0.14m, total),
        TableName: "Mesa 04",
        OrderFolio: "#ORD-9938",
        WaiterName: "Carlos R.");

    private sealed class ThrowingCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException("Redis connection timed out.");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis connection timed out.");
        public void Refresh(string key) => throw new TimeoutException("Redis connection timed out.");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis connection timed out.");
        public void Remove(string key) => throw new TimeoutException("Redis connection timed out.");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis connection timed out.");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new TimeoutException("Redis connection timed out.");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new TimeoutException("Redis connection timed out.");
    }
}
