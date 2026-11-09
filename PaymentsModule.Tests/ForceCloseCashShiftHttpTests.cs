using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// Forced closure of cash shifts by supervision through the real HTTP pipeline (PMC-16 / HU-FE-09):
// authorization roles, immutable audit trail, calculation reuse, status transition, error catalog and 500 rollback.
public class ForceCloseCashShiftHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Scenario1_Gerente_executes_force_close_with_shortage_successfully()
    {
        var (shiftId, cashierId) = await SeedShiftWithSalesAsync(
            initialFund: 500m,
            cashSales: 1750m,
            tips: 120m,
            cardSales: 300m);

        using var client = api.CreateAuthenticatedClient("gerente-supervisor", "Gerente");
        var request = new ForceCloseCashShiftRequest(
            2100.00m,
            "El cajero finalizó su turno y abandonó las instalaciones sin ejecutar el corte en el sistema.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{shiftId}/force-close", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ForceCloseCashShiftResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(cashierId, body.CashierName);
        Assert.Equal(CashShiftStatus.Unbalanced, body.Status);
        Assert.Equal(500.00m, body.OpeningFloat);
        Assert.Equal(2250.00m, body.ExpectedCash);
        Assert.Equal(2100.00m, body.DeclaredCash);
        Assert.Equal(-150.00m, body.Difference);
        Assert.Equal(1, body.CardTransactionCount);
        Assert.Equal(120.00m, body.RetainedTips);
        Assert.Equal("Cierre forzado ejecutado exitosamente con faltante de caja.", body.Message);

        // Verify DB persistence and audit trail
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var saved = await db.CashShifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
        Assert.Equal(CashShiftStatus.Unbalanced, saved.Status);
        Assert.Equal("gerente-supervisor", saved.ClosedByAdminId);
        Assert.Equal(request.ForceCloseReason, saved.ForceCloseReason);
        Assert.Equal(-150.00m, saved.Difference);
        Assert.NotNull(saved.ClosedAt);
    }

    [Fact]
    public async Task Scenario1b_Administrador_executes_force_close_with_exact_cash_returns_cerrado()
    {
        var (shiftId, cashierId) = await SeedShiftWithSalesAsync(
            initialFund: 500m,
            cashSales: 200m,
            tips: 0m,
            cardSales: 0m);

        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");
        var request = new ForceCloseCashShiftRequest(
            700.00m,
            "Cierre forzado ejecutado por supervisor con corte exacto.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{shiftId}/force-close", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ForceCloseCashShiftResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(cashierId, body.CashierName);
        Assert.Equal(CashShiftStatus.Closed, body.Status);
        Assert.Equal(500.00m, body.OpeningFloat);
        Assert.Equal(700.00m, body.ExpectedCash);
        Assert.Equal(700.00m, body.DeclaredCash);
        Assert.Equal(0.00m, body.Difference);
        Assert.Equal(0, body.CardTransactionCount);
        Assert.Equal(0.00m, body.RetainedTips);
        Assert.Equal("Cierre forzado ejecutado exitosamente con corte exacto.", body.Message);

        // Verify DB persistence and audit trail
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var saved = await db.CashShifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
        Assert.Equal(CashShiftStatus.Closed, saved.Status);
        Assert.Equal("admin-user", saved.ClosedByAdminId);
        Assert.Equal(request.ForceCloseReason, saved.ForceCloseReason);
        Assert.Equal(0.00m, saved.Difference);
    }

    [Fact]
    public async Task Scenario2_Cajero_attempts_force_close_returns_403_forbidden_role()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync();

        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");
        var request = new ForceCloseCashShiftRequest(
            1000.00m,
            "Intento no autorizado de cerrar caja por parte de cajero.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{shiftId}/force-close", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("FORBIDDEN_ROLE", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));

        // Verify shift remains Open
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var saved = await db.CashShifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
        Assert.Equal(CashShiftStatus.Open, saved.Status);
        Assert.Null(saved.ClosedAt);
    }

    [Fact]
    public async Task Scenario2b_Anonymous_request_returns_401_unauthorized()
    {
        using var client = api.CreateAnonymousClient();
        var request = new ForceCloseCashShiftRequest(1000.00m, "Intento sin autenticación.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{Guid.NewGuid()}/force-close", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scenario3a_Non_existent_shift_id_returns_404_cash_shift_not_found()
    {
        var nonExistentId = Guid.NewGuid();
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");
        var request = new ForceCloseCashShiftRequest(1000.00m, "Turno inexistente en base de datos.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{nonExistentId}/force-close", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("CASH_SHIFT_NOT_FOUND", error.Code);
        Assert.Equal("El turno de caja no existe", error.Message);
    }

    [Fact]
    public async Task Scenario3b_Already_closed_shift_returns_409_cash_shift_closed()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync(initialStatus: CashShiftStatus.Closed);
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");
        var request = new ForceCloseCashShiftRequest(1000.00m, "Intento de forzar cierre sobre turno ya cerrado.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{shiftId}/force-close", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("CASH_SHIFT_CLOSED", error.Code);
        Assert.Equal("El turno de caja ya ha sido cerrado", error.Message);
    }

    [Fact]
    public async Task Scenario4_Database_failure_during_write_executes_rollback_keeps_shift_open_and_returns_500()
    {
        var (shiftId, _) = await SeedShiftWithSalesAsync();

        api.Failures.FailQueriesWith(shiftId);

        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");
        var request = new ForceCloseCashShiftRequest(
            1000.00m,
            "Fallo de infraestructura simulado durante la transacción.");

        var response = await client.PostAsJsonAsync($"/api/v1/payments/cash-shifts/{shiftId}/force-close", request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Simulated SQL Server timeout", body, StringComparison.OrdinalIgnoreCase);

        api.Failures.StopFailing(shiftId);

        // Verify shift remains OPEN due to transaction rollback
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var saved = await db.CashShifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
        Assert.Equal(CashShiftStatus.Open, saved.Status);
        Assert.Null(saved.ClosedAt);
        Assert.Null(saved.ClosedByAdminId);
    }

    private async Task<(Guid ShiftId, string CashierId)> SeedShiftWithSalesAsync(
        CashShiftStatus initialStatus = CashShiftStatus.Open,
        decimal initialFund = 500m,
        decimal cashSales = 0m,
        decimal tips = 0m,
        decimal cardSales = 0m)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var cashierId = $"cashier-{Guid.NewGuid()}";
        var shift = new CashShift
        {
            CashierId = cashierId,
            InitialFund = initialFund,
            Status = initialStatus,
            OpenedAt = DateTimeOffset.UtcNow.AddHours(-3),
            ClosedAt = initialStatus != CashShiftStatus.Open ? DateTimeOffset.UtcNow.AddHours(-1) : null
        };
        db.CashShifts.Add(shift);

        if (cashSales > 0m)
        {
            var number = Random.Shared.Next(1000000, 9999999);
            var now = DateTimeOffset.UtcNow.AddMinutes(-40);
            var sale = new PaymentTransaction
            {
                Number = number,
                Folio = $"TX-{now:yyyyMMdd}-{number:D6}",
                DinningSessionId = Guid.NewGuid(),
                CashShiftId = shift.Id,
                CashierId = cashierId,
                Subtotal = cashSales,
                Tip = 0m,
                Total = cashSales,
                Status = TransactionStatus.Completed,
                CreatedAt = now,
                Payments = [new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = cashSales }]
            };
            db.Transactions.Add(sale);
        }

        if (cardSales > 0m || tips > 0m)
        {
            var number = Random.Shared.Next(1000000, 9999999);
            var now = DateTimeOffset.UtcNow.AddMinutes(-30);
            var totalCard = cardSales + tips;
            var sale = new PaymentTransaction
            {
                Number = number,
                Folio = $"TX-{now:yyyyMMdd}-{number:D6}",
                DinningSessionId = Guid.NewGuid(),
                CashShiftId = shift.Id,
                CashierId = cashierId,
                Subtotal = cardSales,
                Tip = tips,
                Total = totalCard,
                Status = TransactionStatus.Completed,
                CreatedAt = now,
                Payments = [new PaymentBreakdown { Method = PaymentMethod.CreditCard, Amount = totalCard, AuthorizationReference = "AUTH-456" }]
            };
            db.Transactions.Add(sale);
        }

        await db.SaveChangesAsync();
        return (shift.Id, cashierId);
    }
}
