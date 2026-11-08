using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// Supervision shift summary by id through the real HTTP pipeline (PMC-31 / PMC-16):
// authorization roles, input validation, 404/403/400 error bodies, and MVC JSON serialization.
public class CashShiftSummaryByIdHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Administrator_queries_open_shift_returns_200_with_summary()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync(CashShiftStatus.Open, initialFund: 500m, cashAmount: 200m, cardAmount: 350m);
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(CashShiftStatus.Open, body.ShiftStatus);
        Assert.Equal(500.00m, body.InitialFund);
        Assert.Equal(200.00m, body.Income.CashCollectedTotal);
        Assert.Equal(350.00m, body.Income.CardsTotal);
        Assert.Equal(700.00m, body.ExpectedCashInDrawer);
    }

    [Fact]
    public async Task Gerente_queries_open_shift_returns_200_with_summary()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync(CashShiftStatus.Open, initialFund: 400m, cashAmount: 150m, cardAmount: 100m);
        using var client = api.CreateAuthenticatedClient("gerente-user", "Gerente");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(CashShiftStatus.Open, body.ShiftStatus);
        Assert.Equal(400.00m, body.InitialFund);
        Assert.Equal(150.00m, body.Income.CashCollectedTotal);
        Assert.Equal(100.00m, body.Income.CardsTotal);
        Assert.Equal(550.00m, body.ExpectedCashInDrawer);
    }

    [Fact]
    public async Task Admin_role_variant_queries_open_shift_returns_200()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync(CashShiftStatus.Open, initialFund: 300m, cashAmount: 50m, cardAmount: 0m);
        using var client = api.CreateAuthenticatedClient("admin-variant", "Admin");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
    }

    [Fact]
    public async Task Cashier_role_returns_403_forbidden_role()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync();
        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("FORBIDDEN_ROLE", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Request_without_token_returns_401()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{Guid.NewGuid()}/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Non_existent_shift_id_returns_404_cash_shift_not_found()
    {
        var nonExistentId = Guid.NewGuid();
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{nonExistentId}/summary");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("CASH_SHIFT_NOT_FOUND", error.Code);
        Assert.Equal("El turno de caja no existe", error.Message);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_guid_format_returns_400_validation_error(string invalidId)
    {
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{invalidId}/summary");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorResponse.ValidationErrorCode, error.Code);
    }

    [Fact]
    public async Task Closed_shift_returns_200_with_final_summary_and_cerrado_status()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync(CashShiftStatus.Closed, initialFund: 500m, cashAmount: 200m, cardAmount: 100m);
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(CashShiftStatus.Closed, body.ShiftStatus);
        Assert.Equal(500.00m, body.InitialFund);
        Assert.Equal(200.00m, body.Income.CashCollectedTotal);
        Assert.Equal(100.00m, body.Income.CardsTotal);
        Assert.Equal(700.00m, body.ExpectedCashInDrawer);
    }

    [Fact]
    public async Task Unbalanced_shift_returns_200_with_final_summary_and_descuadrado_status()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync(CashShiftStatus.Unbalanced, initialFund: 500m, cashAmount: 200m, cardAmount: 50m);
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(CashShiftStatus.Unbalanced, body.ShiftStatus);
        Assert.Equal(500.00m, body.InitialFund);
        Assert.Equal(200.00m, body.Income.CashCollectedTotal);
        Assert.Equal(50.00m, body.Income.CardsTotal);
        Assert.Equal(700.00m, body.ExpectedCashInDrawer);
    }

    [Fact]
    public async Task Coherence_with_pmc9_open_shift_returns_identical_amounts_to_owner_cashier_and_admin()
    {
        var (shiftId, cashierId) = await SeedShiftWithSalesAsync(CashShiftStatus.Open, initialFund: 500m, cashAmount: 320m, cardAmount: 180m);

        using var cashierClient = api.CreateAuthenticatedClient(cashierId, "Cajero");
        var cashierResponse = await cashierClient.GetAsync("/api/v1/payments/cash-shifts/summary");
        Assert.Equal(HttpStatusCode.OK, cashierResponse.StatusCode);
        var cashierSummary = await cashierResponse.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();

        using var adminClient = api.CreateAuthenticatedClient("admin-user", "Administrador");
        var adminResponse = await adminClient.GetAsync($"/api/v1/payments/cash-shifts/{shiftId}/summary");
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        var adminSummary = await adminResponse.Content.ReadFromJsonAsync<CashShiftSummaryResponse>();

        Assert.NotNull(cashierSummary);
        Assert.NotNull(adminSummary);
        Assert.Equal(cashierSummary.CashShiftId, adminSummary.CashShiftId);
        Assert.Equal(cashierSummary.ShiftStatus, adminSummary.ShiftStatus);
        Assert.Equal(cashierSummary.InitialFund, adminSummary.InitialFund);
        Assert.Equal(cashierSummary.Income.CashCollectedTotal, adminSummary.Income.CashCollectedTotal);
        Assert.Equal(cashierSummary.Income.CardsTotal, adminSummary.Income.CardsTotal);
        Assert.Equal(cashierSummary.ExpectedCashInDrawer, adminSummary.ExpectedCashInDrawer);
    }

    private async Task<(Guid ShiftId, string CashierId)> SeedShiftWithSalesAsync(
        CashShiftStatus status = CashShiftStatus.Open,
        decimal initialFund = 500m,
        decimal cashAmount = 200m,
        decimal cardAmount = 350m)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var cashierId = $"cashier-{Guid.NewGuid()}";
        var shift = new CashShift
        {
            CashierId = cashierId,
            InitialFund = initialFund,
            Status = status,
            OpenedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ClosedAt = status != CashShiftStatus.Open ? DateTimeOffset.UtcNow.AddHours(-1) : null
        };
        db.CashShifts.Add(shift);

        if (cashAmount > 0m || cardAmount > 0m)
        {
            var payments = new List<PaymentBreakdown>();
            if (cashAmount > 0m)
            {
                payments.Add(new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = cashAmount });
            }
            if (cardAmount > 0m)
            {
                payments.Add(new PaymentBreakdown { Method = PaymentMethod.CreditCard, Amount = cardAmount });
            }

            var number = Random.Shared.Next(1000000, 9999999);
            var now = DateTimeOffset.UtcNow.AddMinutes(-30);
            var sale = new PaymentTransaction
            {
                Number = number,
                Folio = $"TX-{now:yyyyMMdd}-{number:D6}",
                DinningSessionId = Guid.NewGuid(),
                CashShiftId = shift.Id,
                CashierId = cashierId,
                Subtotal = cashAmount + cardAmount,
                Total = cashAmount + cardAmount,
                Status = TransactionStatus.Completed,
                CreatedAt = now,
                Payments = payments
            };
            db.Transactions.Add(sale);
        }

        await db.SaveChangesAsync();
        return (shift.Id, cashierId);
    }
}
