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

public class DinningSessionReadyConsumerTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddLogging()
        .AddDistributedMemoryCache()
        .AddScoped<IValidator<DinningSessionReadyEvent>, DinningSessionReadyEventValidator>()
        .AddScoped<PendingBillService>()
        .AddMassTransitTestHarness(x => x.AddConsumer<DinningSessionReadyConsumer, DinningSessionReadyConsumerDefinition>())
        .BuildServiceProvider(true);

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public Task InitializeAsync() => Harness.Start();

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Persists_snapshot_of_new_bill()
    {
        var message = NewEvent(Guid.NewGuid(), total: 237.80m);

        var fault = await PublishAsync(message);

        Assert.Null(fault);
        Assert.Equal(237.80m, (await GetAsync(message.DinningSessionId))?.Totals.ExpectedTotal);
    }

    [Fact]
    public async Task Ignores_duplicate_event_without_error()
    {
        var session = Guid.NewGuid();
        var original = NewEvent(session, total: 200m);
        await PublishAsync(original);
        await PublishAsync(NewEvent(session, total: 250m));

        // Redelivery of an already processed event must not overwrite the newer snapshot.
        var fault = await PublishAsync(original);

        Assert.Null(fault);
        Assert.Equal(250m, (await GetAsync(session))?.Totals.ExpectedTotal);
    }

    [Fact]
    public async Task Overwrites_snapshot_of_same_session()
    {
        var session = Guid.NewGuid();
        await PublishAsync(NewEvent(session, total: 200m));

        await PublishAsync(NewEvent(session, total: 250m));

        Assert.Equal(250m, (await GetAsync(session))?.Totals.ExpectedTotal);
    }

    [Fact]
    public async Task Discards_event_older_than_snapshot()
    {
        var session = Guid.NewGuid();
        var newer = NewEvent(session, total: 250m);
        await PublishAsync(newer);

        var fault = await PublishAsync(NewEvent(session, total: 200m) with { Timestamp = newer.Timestamp.AddMinutes(-1) });

        Assert.Null(fault);
        Assert.Equal(250m, (await GetAsync(session))?.Totals.ExpectedTotal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Rejects_invalid_total_without_persisting(decimal total)
    {
        var message = NewEvent(Guid.NewGuid(), total);

        var fault = await PublishAsync(message);

        Assert.IsType<ValidationException>(fault);
        Assert.Null(await GetAsync(message.DinningSessionId));
    }

    [Fact]
    public async Task Rejects_bill_without_items()
    {
        var message = NewEvent(Guid.NewGuid(), 100m) with { Items = [] };

        Assert.IsType<ValidationException>(await PublishAsync(message));
    }

    [Fact]
    public void Keeps_wire_contract_agreed_with_ordenes()
    {
        var json = JsonSerializer.Serialize(NewEvent(Guid.NewGuid(), 100m), SystemTextJsonMessageSerializer.Options);

        foreach (var name in new[] { "mesaId", "meseroId", "totales", "totalEsperado", "impuestos", "platilloId", "nombre", "cantidad", "precioUnitario", "importe" })
        {
            Assert.Contains($"\"{name}\"", json);
        }
    }

    private async Task<Exception?> PublishAsync(DinningSessionReadyEvent message)
    {
        var consumed = Harness.GetConsumerHarness<DinningSessionReadyConsumer>().Consumed;
        IReceivedMessage<DinningSessionReadyEvent>[] Received() =>
            consumed.Select<DinningSessionReadyEvent>(m => m.Context.Message.EventId == message.EventId).ToArray();

        var before = Received().Length;
        await Harness.Bus.Publish(message);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Received().Length == before)
        {
            await Task.Delay(20, timeout.Token);
        }

        return Received().Last().Exception;
    }

    private async Task<DinningSessionReadyEvent?> GetAsync(Guid session)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PendingBillService>().GetAsync(session);
    }

    private static DinningSessionReadyEvent NewEvent(Guid session, decimal total) => new(
        Guid.NewGuid(),
        session,
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        [new BillItem(Guid.NewGuid(), "Tacos al Pastor", 2, 60m, 120m)],
        new BillTotals(total, 0m, total));
}
