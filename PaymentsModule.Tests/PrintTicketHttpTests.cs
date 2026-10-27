using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// PMC-14 through the real HTTP pipeline: authentication, UseExceptionHandler and MVC's JSON serializer included.
public class PrintTicketHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await ChargeServiceTests.EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Without token -> 401, on a real request instead of only checking for the [Authorize] attribute.
    [Fact]
    public async Task Request_without_token_returns_401()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/pagos/TX-00000000-000000/ticket-impresion");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Scenario 3: a database timeout is handled by UseExceptionHandler -> 500 without SQL Server internals.
    [Fact]
    public async Task Database_timeout_returns_500_without_internal_details()
    {
        var folio = $"TX-FAIL-{Guid.NewGuid():N}";
        api.Failures.FailQueriesWith(folio);
        using var client = api.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/pagos/{folio}/ticket-impresion");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Simulated SQL Server timeout", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(SimulatedDatabaseTimeout), body, StringComparison.Ordinal);
    }

    // Contract as the printer receives it, serialized by MVC: Spanish snake_case names, enum labels, 2 decimals.
    [Fact]
    public async Task Existing_folio_returns_the_agreed_json_over_http()
    {
        var soldAt = new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);
        PaymentTransaction sale;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            sale = await PrintTicketTests.SeedSaleAsync(
                scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), soldAt, TransactionStatus.Completed);
        }

        using var client = api.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/pagos/{sale.Folio}/ticket-impresion");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $$$"""
            {"cabecera":{"folio":"{{{sale.Folio}}}","fecha_impresion":"2026-09-29T18:00:00+00:00","turno_caja_id":"{{{sale.CashShiftId}}}","dinning_session_id":"{{{sale.DinningSessionId}}}"},"cuerpo":[{"nombre_platillo":"Corte Ribeye","cantidad":1,"precio_unitario":500.00,"importe":500.00},{"nombre_platillo":"Agua de horchata","cantidad":2,"precio_unitario":35.50,"importe":71.00}],"pie":{"subtotal":571.00,"impuestos":91.36,"propina":60.00,"total":722.36,"pagos":[{"metodo_pago":"TARJETA_CREDITO","monto":400.00},{"metodo_pago":"EFECTIVO","monto":322.36}],"estado":"COMPLETADO"}}
            """,
            await response.Content.ReadAsStringAsync());
    }
}
