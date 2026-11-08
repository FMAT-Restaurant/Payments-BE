using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// HU: Consulta de Turnos de Caja Abiertos para Supervisión (PMC-30) / GET /api/v1/payments/cash-shifts/open
// through the real HTTP pipeline: RBAC authorization, ordering by opened_at, 403, 401, and JSON snake_case DTO contract.
public class OpenCashShiftsHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Gerente_queries_open_shifts_returns_200_ordered_by_opened_at_ascending()
    {
        var cashier1 = $"cashier-older-{Guid.NewGuid()}";
        var cashier2 = $"cashier-newer-{Guid.NewGuid()}";

        var (shiftId1, openedAt1, float1) = await SeedShiftAsync(cashier1, CashShiftStatus.Open, 1000.00m, DateTimeOffset.UtcNow.AddHours(-3));
        var (shiftId2, openedAt2, float2) = await SeedShiftAsync(cashier2, CashShiftStatus.Open, 500.00m, DateTimeOffset.UtcNow.AddHours(-1));
        // Seed a closed shift that must NOT appear in the list
        await SeedShiftAsync($"cashier-closed-{Guid.NewGuid()}", CashShiftStatus.Closed, 700.00m, DateTimeOffset.UtcNow.AddHours(-4));

        using var client = api.CreateAuthenticatedClient("gerente-user", "Gerente");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/open");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var array = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, array.ValueKind);

        var items = await response.Content.ReadFromJsonAsync<List<OpenShiftListItemResponse>>();
        Assert.NotNull(items);

        var item1 = items.FirstOrDefault(s => s.CashShiftId == shiftId1);
        var item2 = items.FirstOrDefault(s => s.CashShiftId == shiftId2);
        Assert.NotNull(item1);
        Assert.NotNull(item2);

        Assert.Equal(cashier1, item1.CashierName);
        Assert.Equal(float1, item1.OpeningFloat);
        Assert.Equal(cashier2, item2.CashierName);
        Assert.Equal(float2, item2.OpeningFloat);

        var index1 = items.IndexOf(item1);
        var index2 = items.IndexOf(item2);
        Assert.True(index1 < index2, "Older open shift must appear before newer open shift.");
    }

    [Fact]
    public async Task Administrador_queries_open_shifts_returns_200()
    {
        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/open");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<OpenShiftListItemResponse>>();
        Assert.NotNull(list);
    }

    [Fact]
    public async Task Admin_role_variant_queries_open_shifts_returns_200()
    {
        using var client = api.CreateAuthenticatedClient("admin-variant", "Admin");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/open");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cajero_role_returns_403_forbidden_role()
    {
        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("FORBIDDEN_ROLE", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Request_without_token_returns_401_unauthorized()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/open");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dto_serializes_to_strict_snake_case_contract()
    {
        var cashierId = $"cashier-snake-{Guid.NewGuid()}";
        var (shiftId, openedAt, openingFloat) = await SeedShiftAsync(cashierId, CashShiftStatus.Open, 1000.00m, DateTimeOffset.UtcNow.AddMinutes(-10));

        using var client = api.CreateAuthenticatedClient("admin-user", "Administrador");

        var response = await client.GetAsync("/api/v1/payments/cash-shifts/open");
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var targetElement = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("cash_shift_id").GetString() == shiftId.ToString());

        Assert.True(targetElement.TryGetProperty("cash_shift_id", out _));
        Assert.True(targetElement.TryGetProperty("cashier_name", out var cashierNameProp));
        Assert.Equal(cashierId, cashierNameProp.GetString());
        Assert.True(targetElement.TryGetProperty("opened_at", out _));
        Assert.True(targetElement.TryGetProperty("opening_float", out var floatProp));
        Assert.Equal(openingFloat, floatProp.GetDecimal());
    }

    private async Task<(Guid ShiftId, DateTimeOffset OpenedAt, decimal OpeningFloat)> SeedShiftAsync(
        string cashierId,
        CashShiftStatus status,
        decimal initialFund,
        DateTimeOffset openedAt)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
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
