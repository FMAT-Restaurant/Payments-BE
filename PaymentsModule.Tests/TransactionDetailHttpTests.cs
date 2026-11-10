using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// Transaction detail through the real HTTP pipeline (PMC-11 / PMC-25):
// authentication, 200 OK wire contract, 404 TRANSACTION_NOT_FOUND, and 500 timeout without internals.
public class TransactionDetailHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Scenario1_Existing_folio_returns_200_with_full_detail()
    {
        var folio = "TX-20261107-000042";
        var paidAt = new DateTimeOffset(2026, 11, 7, 14, 30, 0, TimeSpan.Zero);
        var (shiftId, sessionId) = await SeedSaleAsync(folio, paidAt, subtotal: 646.55m, taxes: 103.45m, tip: 50.00m, changeGiven: 0.00m);

        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/{folio}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TransactionDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(folio, body.Folio);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(sessionId, body.DinningSessionId);
        Assert.Equal(TransactionStatus.Completed, body.Status);
        Assert.Equal(paidAt, body.PaidAt);
        Assert.Equal(646.55m, body.Subtotal);
        Assert.Equal(103.45m, body.Taxes);
        Assert.Equal(50.00m, body.Tip);
        Assert.Equal(800.00m, body.Total);
        Assert.Equal(0.00m, body.ChangeGiven);

        Assert.Equal(2, body.Items.Count);
        Assert.Equal("Corte Ribeye", body.Items[0].DishName);
        Assert.Equal(1, body.Items[0].Quantity);
        Assert.Equal(500.00m, body.Items[0].UnitPrice);
        Assert.Equal(500.00m, body.Items[0].Amount);
        Assert.Equal("Vino Tinto", body.Items[1].DishName);
        Assert.Equal(1, body.Items[1].Quantity);
        Assert.Equal(250.00m, body.Items[1].UnitPrice);
        Assert.Equal(250.00m, body.Items[1].Amount);

        var payment = Assert.Single(body.Payments);
        Assert.Equal(PaymentMethod.CreditCard, payment.Method);
        Assert.Equal(800.00m, payment.Amount);
        Assert.Equal("AUTH-123456", payment.AuthorizationReference);
    }

    [Fact]
    public async Task Scenario2_Non_existent_folio_returns_404_transaction_not_found()
    {
        var nonExistentFolio = "TX-99999999-999999";
        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/{nonExistentFolio}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("TRANSACTION_NOT_FOUND", error.Code);
        Assert.Equal($"El ticket '{nonExistentFolio}' no existe", error.Message);
    }

    [Fact]
    public async Task Scenario3_Database_timeout_returns_500_without_internal_details()
    {
        var folio = $"TX-FAIL-{Guid.NewGuid():N}";
        api.Failures.FailQueriesWith(folio);

        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/{folio}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Simulated SQL Server timeout", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(SimulatedDatabaseTimeout), body, StringComparison.Ordinal);

        api.Failures.StopFailing(folio);
    }

    [Fact]
    public async Task Request_without_token_returns_401_unauthorized()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/payments/TX-20261107-000042");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<(Guid ShiftId, Guid SessionId)> SeedSaleAsync(
        string folio,
        DateTimeOffset paidAt,
        decimal subtotal,
        decimal taxes,
        decimal tip,
        decimal changeGiven)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var shift = new CashShift
        {
            CashierId = "cashier-test",
            InitialFund = 500m,
            Status = CashShiftStatus.Open,
            OpenedAt = paidAt.AddHours(-1)
        };
        db.CashShifts.Add(shift);

        var sessionId = Guid.NewGuid();
        var total = subtotal + taxes + tip;
        var sale = new PaymentTransaction
        {
            Number = Random.Shared.Next(1000000, 9999999),
            Folio = folio,
            DinningSessionId = sessionId,
            CashShiftId = shift.Id,
            CashierId = shift.CashierId,
            Subtotal = subtotal,
            Taxes = taxes,
            Tip = tip,
            Total = total,
            ChangeGiven = changeGiven,
            Status = TransactionStatus.Completed,
            CreatedAt = paidAt,
            Items =
            [
                new TransactionItem { DishName = "Corte Ribeye", Quantity = 1, UnitPrice = 500.00m, Amount = 500.00m },
                new TransactionItem { DishName = "Vino Tinto", Quantity = 1, UnitPrice = 250.00m, Amount = 250.00m }
            ],
            Payments =
            [
                new PaymentBreakdown { Method = PaymentMethod.CreditCard, Amount = total, AuthorizationReference = "AUTH-123456" }
            ]
        };
        db.Transactions.Add(sale);
        await db.SaveChangesAsync();

        return (shift.Id, sessionId);
    }
}
