using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// PMC-14 through the real HTTP pipeline: authentication, UseExceptionHandler and MVC's JSON serializer included.
public class PrintTicketHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Without token -> 401, on a real request instead of only checking for the [Authorize] attribute.
    [Fact]
    public async Task Request_without_token_returns_401()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/payments/TX-00000000-000000/ticket");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Scenario 2: unknown folio returns 404 TRANSACTION_NOT_FOUND over HTTP.
    [Fact]
    public async Task Unknown_folio_returns_404_over_http()
    {
        using var client = api.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/payments/TX-99999999-999999/ticket");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TRANSACTION_NOT_FOUND", body);
        Assert.Contains("El ticket 'TX-99999999-999999' no existe", body);
    }

    // Scenario 3: a database timeout is handled by UseExceptionHandler -> 500 without SQL Server internals.
    [Fact]
    public async Task Database_timeout_returns_500_without_internal_details()
    {
        var folio = $"TX-FAIL-{Guid.NewGuid():N}";
        api.Failures.FailQueriesWith(folio);
        using var client = api.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/payments/{folio}/ticket");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Simulated SQL Server timeout", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(SimulatedDatabaseTimeout), body, StringComparison.Ordinal);
        Assert.Contains("INTERNAL_ERROR", body);
    }

    // Contract as the browser receives it, serialized by MVC: flat structure, English property names, enum labels, 2 decimals.
    [Fact]
    public async Task Existing_folio_returns_the_agreed_json_over_http()
    {
        var soldAt = new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);
        PaymentTransaction sale;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            sale = await PrintTicketTests.SeedSaleAsync(
                scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), soldAt, TransactionStatus.Completed);
        }

        using var client = api.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/v1/payments/{sale.Folio}/ticket");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $$$"""
            {"restaurant_name":"Restaurante FMAT","folio":"{{{sale.Folio}}}","paid_at":"2026-11-07T18:00:00+00:00","table_name":"Mesa 04","cashier_name":"Juan Pérez","items":[{"dish_name":"Corte Ribeye","quantity":1,"unit_price":500.00,"amount":500.00},{"dish_name":"Agua de horchata","quantity":2,"unit_price":35.50,"amount":71.00}],"subtotal":571.00,"taxes":91.36,"tip":60.00,"total":722.36,"payments":[{"method":"TARJETA_CREDITO","amount":400.00,"authorization_reference":"AUTH-987654"},{"method":"EFECTIVO","amount":322.36,"authorization_reference":null}],"change_given":50.00}
            """,
            await response.Content.ReadAsStringAsync());
    }

    // Scenario 4: concurrent reprints over HTTP receive 200 OK without blocking.
    [Fact]
    public async Task Concurrent_reprints_over_http_return_200_without_blocking()
    {
        var soldAt = new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);
        PaymentTransaction sale;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            sale = await PrintTicketTests.SeedSaleAsync(
                scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), soldAt, TransactionStatus.Completed);
        }

        using var client = api.CreateAuthenticatedClient();

        var tasks = Enumerable.Range(0, 5).Select(_ => client.GetAsync($"/api/v1/payments/{sale.Folio}/ticket"));
        var responses = await Task.WhenAll(tasks);

        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
    }
}
