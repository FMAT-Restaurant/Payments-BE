using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// HU: Consulta de turno operativo actual del usuario (Bandeja de Caja) / GET /api/v1/payments/cash-shifts/me
// through the real HTTP pipeline: JWT identity extraction, authorization roles, 404, 401, 403, and JSON snake_case DTO contract.
public class ActiveCashShiftHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Cajero_with_open_shift_returns_200_with_snake_case_dto()
    {
        var cashierId = $"cashier-{Guid.NewGuid()}";
        var (shiftId, openedAt, openingFloat) = await SeedShiftAsync(cashierId, CashShiftStatus.Open, 1000.00m);
        using var client = api.CreateAuthenticatedClient(cashierId, "Cajero");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(shiftId.ToString(), root.GetProperty("cash_shift_id").GetString());
        Assert.Equal(openingFloat, root.GetProperty("opening_float").GetDecimal());
        Assert.True(root.TryGetProperty("opened_at", out var openedAtProp));
        Assert.Equal(openedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), openedAtProp.GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"));
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("Gerente")]
    public async Task Management_roles_with_open_shift_return_200(string role)
    {
        var userId = $"supervisor-{Guid.NewGuid()}";
        var (shiftId, _, openingFloat) = await SeedShiftAsync(userId, CashShiftStatus.Open, 750.50m);
        using var client = api.CreateAuthenticatedClient(userId, role);

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ActiveCashShiftResponse>();
        Assert.NotNull(body);
        Assert.Equal(shiftId, body.CashShiftId);
        Assert.Equal(openingFloat, body.OpeningFloat);
    }

    [Fact]
    public async Task Cajero_without_open_shift_returns_404_not_found()
    {
        var cashierId = $"cashier-new-{Guid.NewGuid()}";
        using var client = api.CreateAuthenticatedClient(cashierId, "Cajero");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/me");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("CASH_SHIFT_NOT_OPEN", error.Code);
        Assert.Equal("No hay un turno de caja abierto para este usuario", error.Message);
    }

    [Fact]
    public async Task Cajero_with_closed_shift_from_yesterday_returns_404_not_found()
    {
        var cashierId = $"cashier-yesterday-{Guid.NewGuid()}";
        await SeedShiftAsync(cashierId, CashShiftStatus.Closed, 500.00m);
        using var client = api.CreateAuthenticatedClient(cashierId, "Cajero");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/me");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("CASH_SHIFT_NOT_OPEN", error.Code);
    }

    [Fact]
    public async Task Request_without_token_returns_401_unauthorized()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_role_returns_403_forbidden_role()
    {
        var userId = $"mesero-{Guid.NewGuid()}";
        await SeedShiftAsync(userId, CashShiftStatus.Open, 500.00m);
        using var client = api.CreateAuthenticatedClient(userId, "Mesero");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("FORBIDDEN_ROLE", error.Code);
    }

    [Fact]
    public async Task Identity_is_strictly_extracted_from_jwt_ignoring_url_params_and_client_headers()
    {
        var legitimateUser = $"cashier-legit-{Guid.NewGuid()}";
        var spoofedUser = $"cashier-spoofed-{Guid.NewGuid()}";

        var (legitShiftId, _, _) = await SeedShiftAsync(legitimateUser, CashShiftStatus.Open, 1000.00m);
        await SeedShiftAsync(spoofedUser, CashShiftStatus.Open, 2000.00m);

        using var client = api.CreateAuthenticatedClient(legitimateUser, "Cajero");
        // Injecting spoofed headers and query params that client might try to pass
        client.DefaultRequestHeaders.Add("X-User-Id", spoofedUser);

        var response = await client.GetAsync($"/api/v1/payments/cash-shifts/me?user_id={spoofedUser}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ActiveCashShiftResponse>();
        Assert.NotNull(body);
        // The backend must resolve the legitimate user from JWT, not the spoofed header or query param.
        Assert.Equal(legitShiftId, body.CashShiftId);
        Assert.Equal(1000.00m, body.OpeningFloat);
    }

    private async Task<(Guid ShiftId, DateTimeOffset OpenedAt, decimal OpeningFloat)> SeedShiftAsync(
        string cashierId,
        CashShiftStatus status,
        decimal initialFund)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var shift = new CashShift
        {
            CashierId = cashierId,
            InitialFund = initialFund,
            Status = status,
            OpenedAt = openedAt,
            ClosedAt = status != CashShiftStatus.Open ? DateTimeOffset.UtcNow : null
        };
        db.CashShifts.Add(shift);
        await db.SaveChangesAsync();

        return (shift.Id, openedAt, initialFund);
    }
}

