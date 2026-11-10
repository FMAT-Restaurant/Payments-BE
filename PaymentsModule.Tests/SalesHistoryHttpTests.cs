using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// Sales history through the real HTTP pipeline (PMC-10 / PMC-24):
// authentication, semi-open date filtering, pagination, 400 validation, 500 on timeout, and uncommitted transaction isolation.
public class SalesHistoryHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Scenario1_Paged_query_with_date_range_returns_200_and_paged_response()
    {
        var septemberStart = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var octoberStart = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Seed 12 sales in September
        for (var i = 1; i <= 12; i++)
        {
            await SeedSaleAsync($"TX-SEPT-{i:D6}", septemberStart.AddDays(i));
        }

        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync(
            $"/api/v1/payments?start_date={septemberStart:yyyy-MM-ddTHH:mm:ssZ}&end_date={octoberStart:yyyy-MM-ddTHH:mm:ssZ}&page_index=1&page_size=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResponse<SalesHistoryItem>>();
        Assert.NotNull(body);
        Assert.Equal(1, body.PageIndex);
        Assert.Equal(10, body.PageSize);
        Assert.True(body.TotalCount >= 12);
        Assert.Equal(10, body.Data.Count);

        var first = body.Data[0];
        Assert.False(string.IsNullOrWhiteSpace(first.Folio));
        Assert.NotEqual(Guid.Empty, first.CashShiftId);
        Assert.Equal(TransactionStatus.Completed, first.Status);
        Assert.True(first.Total > 0m);

        Assert.NotEmpty(first.Items);
        Assert.Contains(first.Items, item => item.Quantity > 0 && item.UnitPrice > 0m && !string.IsNullOrWhiteSpace(item.DishName));

        Assert.NotEmpty(first.Payments);
        Assert.Contains(first.Payments, p => p.Amount > 0m);
    }

    [Fact]
    public async Task Scenario2_Illogical_date_range_returns_400_without_touching_database()
    {
        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync(
            "/api/v1/payments?start_date=2026-10-01T00:00:00Z&end_date=2026-09-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorResponse.ValidationErrorCode, error.Code);
        Assert.Contains("La fecha de inicio debe ser anterior a la fecha de fin", error.Message);
    }

    [Fact]
    public async Task Scenario3_Database_failure_returns_500_without_internal_details()
    {
        var failDate = new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero);
        api.Failures.FailQueriesWith(failDate);

        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");

        var response = await client.GetAsync(
            $"/api/v1/payments?start_date={failDate:yyyy-MM-ddTHH:mm:ssZ}&end_date=2026-05-06T00:00:00Z&page_index=1&page_size=10");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Simulated SQL Server timeout", body, StringComparison.OrdinalIgnoreCase);

        api.Failures.StopFailing(failDate);
    }

    [Fact]
    public async Task Scenario4_Uncommitted_transaction_in_progress_is_not_visible_due_to_rcsi()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        var uncommittedFolio = $"TX-UC-{Guid.NewGuid():N}"[..30];
        var shift = new CashShift { CashierId = "cashier-uncommitted", InitialFund = 500m };
        db.CashShifts.Add(shift);
        db.Transactions.Add(new PaymentTransaction
        {
            Number = Random.Shared.Next(1000000, 9999999),
            Folio = uncommittedFolio,
            DinningSessionId = Guid.NewGuid(),
            CashShiftId = shift.Id,
            CashierId = shift.CashierId,
            Subtotal = 999m,
            Total = 999m,
            Status = TransactionStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            Items = [new TransactionItem { DishName = "In Progress Dish", Quantity = 1, UnitPrice = 999m, Amount = 999m }],
            Payments = [new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = 999m }]
        });
        await db.SaveChangesAsync();

        // While tx is uncommitted, query sales history from the HTTP endpoint
        using var client = api.CreateAuthenticatedClient("cashier-user", "Cajero");
        var response = await client.GetAsync("/api/v1/payments?page_index=1&page_size=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResponse<SalesHistoryItem>>();
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Data, s => s.Folio == uncommittedFolio);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Request_without_token_returns_401_unauthorized()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/payments?page_index=1&page_size=10");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task SeedSaleAsync(string folio, DateTimeOffset createdAt)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var shift = await db.CashShifts.FirstOrDefaultAsync(s => s.CashierId == "cashier-seed" && s.Status == CashShiftStatus.Open);
        if (shift is null)
        {
            shift = new CashShift { CashierId = "cashier-seed", InitialFund = 500m, OpenedAt = createdAt.AddHours(-1) };
            db.CashShifts.Add(shift);
        }

        db.Transactions.Add(new PaymentTransaction
        {
            Number = Random.Shared.Next(1000000, 9999999),
            Folio = folio,
            DinningSessionId = Guid.NewGuid(),
            CashShiftId = shift.Id,
            CashierId = shift.CashierId,
            Subtotal = 500m,
            Total = 500m,
            Status = TransactionStatus.Completed,
            CreatedAt = createdAt,
            Items = [new TransactionItem { DishName = "Corte Ribeye", Quantity = 1, UnitPrice = 500m, Amount = 500m }],
            Payments = [new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = 500m }]
        });
        await db.SaveChangesAsync();
    }
}
