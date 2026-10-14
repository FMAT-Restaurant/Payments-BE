using FluentValidation;
using MassTransit;
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
        .AddScoped<CuentaPendienteService>()
        .AddMassTransitTestHarness(x => x.AddConsumer<DinningSessionReadyConsumer, DinningSessionReadyConsumerDefinition>())
        .BuildServiceProvider(true);

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public Task InitializeAsync() => Harness.Start();

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Persiste_snapshot_de_cuenta_nueva()
    {
        var evento = Evento(Guid.NewGuid(), total: 237.80m);

        var fault = await PublicarAsync(evento);

        Assert.Null(fault);
        Assert.Equal(237.80m, (await ObtenerAsync(evento.DinningSessionId))?.Totales.TotalEsperado);
    }

    [Fact]
    public async Task Ignora_evento_duplicado_sin_error()
    {
        var sesion = Guid.NewGuid();
        var original = Evento(sesion, total: 200m);
        await PublicarAsync(original);
        await PublicarAsync(Evento(sesion, total: 250m));

        // Redelivery of an already processed event must not overwrite the newer snapshot.
        var fault = await PublicarAsync(original);

        Assert.Null(fault);
        Assert.Equal(250m, (await ObtenerAsync(sesion))?.Totales.TotalEsperado);
    }

    [Fact]
    public async Task Sobreescribe_snapshot_de_la_misma_sesion()
    {
        var sesion = Guid.NewGuid();
        await PublicarAsync(Evento(sesion, total: 200m));

        await PublicarAsync(Evento(sesion, total: 250m));

        Assert.Equal(250m, (await ObtenerAsync(sesion))?.Totales.TotalEsperado);
    }

    [Fact]
    public async Task Descarta_evento_mas_viejo_que_el_snapshot()
    {
        var sesion = Guid.NewGuid();
        var nuevo = Evento(sesion, total: 250m);
        await PublicarAsync(nuevo);

        var fault = await PublicarAsync(Evento(sesion, total: 200m) with { Timestamp = nuevo.Timestamp.AddMinutes(-1) });

        Assert.Null(fault);
        Assert.Equal(250m, (await ObtenerAsync(sesion))?.Totales.TotalEsperado);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Rechaza_total_invalido_sin_persistir(decimal total)
    {
        var evento = Evento(Guid.NewGuid(), total);

        var fault = await PublicarAsync(evento);

        Assert.IsType<ValidationException>(fault);
        Assert.Null(await ObtenerAsync(evento.DinningSessionId));
    }

    [Fact]
    public async Task Rechaza_cuenta_sin_items()
    {
        var evento = Evento(Guid.NewGuid(), 100m) with { Items = [] };

        Assert.IsType<ValidationException>(await PublicarAsync(evento));
    }

    private async Task<Exception?> PublicarAsync(DinningSessionReadyEvent evento)
    {
        var consumed = Harness.GetConsumerHarness<DinningSessionReadyConsumer>().Consumed;
        IReceivedMessage<DinningSessionReadyEvent>[] Recibidos() =>
            consumed.Select<DinningSessionReadyEvent>(m => m.Context.Message.EventId == evento.EventId).ToArray();

        var antes = Recibidos().Length;
        await Harness.Bus.Publish(evento);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Recibidos().Length == antes)
        {
            await Task.Delay(20, timeout.Token);
        }

        return Recibidos().Last().Exception;
    }

    private async Task<DinningSessionReadyEvent?> ObtenerAsync(Guid sesion)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CuentaPendienteService>().ObtenerAsync(sesion);
    }

    private static DinningSessionReadyEvent Evento(Guid sesion, decimal total) => new(
        Guid.NewGuid(),
        sesion,
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        [new ItemCuenta(Guid.NewGuid(), "Tacos al Pastor", 2, 60m, 120m)],
        new TotalesCuenta(total, 0m, total));
}
