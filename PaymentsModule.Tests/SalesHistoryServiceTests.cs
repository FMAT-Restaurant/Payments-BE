using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Controllers;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// Runs against a real SQL Server (see QueriesDatabase).
[Collection(QueriesDatabase.CollectionName)]
public class SalesHistoryServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => await QueriesDatabase.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Pages_the_results_newest_first_and_reports_totals()
    {
        for (var i = 1; i <= 12; i++)
        {
            await QueriesDatabase.SeedAsync($"TX-20260901-{i:D6}", Start.AddMinutes(i));
        }

        var first = await GetAsync(new SalesHistoryFilter(null, null, 1, 10));
        var second = await GetAsync(new SalesHistoryFilter(null, null, 2, 10));

        Assert.Equal((1, 10, 12, 2), (first.PageIndex, first.PageSize, first.TotalCount, first.TotalPages));
        Assert.Equal(10, first.Data.Count);
        Assert.Equal("TX-20260901-000012", first.Data[0].Folio);
        Assert.Equal(2, second.Data.Count);
        Assert.Equal("TX-20260901-000001", second.Data[^1].Folio);
    }

    [Fact]
    public async Task Projects_the_snapshot_lines_with_decimal_prices_and_breakdowns()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Start, TransactionStatus.Completed, (PaymentMethod.Cash, 500m));

        var page = await GetAsync(new SalesHistoryFilter(null, null, 1, 10));

        var row = Assert.Single(page.Data);
        Assert.Equal(500m, row.Total);
        Assert.Equal(TransactionStatus.Completed, row.Status);
        Assert.Equal(Start, row.PaidAt);
        Assert.NotEqual(Guid.Empty, row.CashShiftId);
        var line = Assert.Single(row.Items);
        Assert.Equal(("Ribeye", 1, 500m), (line.DishName, line.Quantity, line.UnitPrice));
        var payment = Assert.Single(row.Payments);
        Assert.Equal((PaymentMethod.Cash, 500m), (payment.Method, payment.Amount));
    }

    [Fact]
    public async Task Filters_by_date_range_as_semi_open_interval_and_excludes_cancelled()
    {
        await QueriesDatabase.SeedAsync("TX-OLD-CASH", Start.AddDays(-30), TransactionStatus.Completed, (PaymentMethod.Cash, 100m));
        await QueriesDatabase.SeedAsync("TX-IN-RANGE-1", Start, TransactionStatus.Completed, (PaymentMethod.Cash, 100m));
        await QueriesDatabase.SeedAsync("TX-IN-RANGE-2", Start.AddHours(2), TransactionStatus.Completed, (PaymentMethod.CreditCard, 100m));
        // Boundary case: exactly at EndDate -> must be excluded because EndDate is exclusive [start, end)
        await QueriesDatabase.SeedAsync("TX-AT-END-BOUNDARY", Start.AddDays(1), TransactionStatus.Completed, (PaymentMethod.Cash, 100m));
        // Cancelled transactions are excluded
        await QueriesDatabase.SeedAsync("TX-CANCELLED", Start.AddHours(1), TransactionStatus.Cancelled, (PaymentMethod.Cash, 100m));

        var range = await GetAsync(new SalesHistoryFilter(Start, Start.AddDays(1), 1, 10));
        var unfiltered = await GetAsync(new SalesHistoryFilter(null, null, 1, 10));

        // Range includes only TX-IN-RANGE-1 and TX-IN-RANGE-2
        Assert.Equal(2, range.TotalCount);
        Assert.Contains(range.Data, s => s.Folio == "TX-IN-RANGE-1");
        Assert.Contains(range.Data, s => s.Folio == "TX-IN-RANGE-2");
        Assert.DoesNotContain(range.Data, s => s.Folio == "TX-AT-END-BOUNDARY");
        Assert.DoesNotContain(range.Data, s => s.Folio == "TX-CANCELLED");

        // Unfiltered includes only the 4 completed ones
        Assert.Equal(4, unfiltered.TotalCount);
        Assert.DoesNotContain(unfiltered.Data, s => s.Folio == "TX-CANCELLED");
    }

    [Fact]
    public async Task Returns_an_empty_page_when_nothing_matches()
    {
        var page = await GetAsync(new SalesHistoryFilter(null, null, 1, 10));

        Assert.Equal((0, 0), (page.TotalCount, page.TotalPages));
        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Controller_answers_400_for_an_invalid_range_and_200_otherwise()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Start);
        await using var db = QueriesDatabase.CreateContext();
        var controller = new SalesHistoryController(new SalesHistoryService(db), new SalesHistoryQueryValidator());

        var invalid = Assert.IsAssignableFrom<ObjectResult>(await controller.Get(
            new SalesHistoryQuery { StartDate = Start.AddDays(1), EndDate = Start }, CancellationToken.None));
        var valid = Assert.IsType<OkObjectResult>(await controller.Get(
            new SalesHistoryQuery { StartDate = Start.AddDays(-1), EndDate = Start.AddDays(1) }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        var error = Assert.IsType<ErrorResponse>(invalid.Value);
        Assert.Equal(ErrorResponse.ValidationErrorCode, error.Code);
        Assert.Contains("La fecha de inicio debe ser anterior a la fecha de fin", error.Message);
        Assert.Equal(1, Assert.IsType<PagedResponse<SalesHistoryItem>>(valid.Value).TotalCount);
    }

    [Fact]
    public async Task Serializes_the_wire_contract_in_english_snake_case()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Start, TransactionStatus.Completed, (PaymentMethod.Cash, 500m));

        var page = await GetAsync(new SalesHistoryFilter(null, null, 1, 10));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        var root = json.RootElement;
        Assert.Equal((1, 10, 1, 1), (
            root.GetProperty("page_index").GetInt32(),
            root.GetProperty("page_size").GetInt32(),
            root.GetProperty("total_count").GetInt32(),
            root.GetProperty("total_pages").GetInt32()));
        var row = root.GetProperty("data")[0];
        Assert.Equal("TX-20260901-000001", row.GetProperty("folio").GetString());
        Assert.True(row.TryGetProperty("cash_shift_id", out _));
        Assert.Equal(500m, row.GetProperty("total").GetDecimal());
        Assert.Equal("COMPLETADO", row.GetProperty("status").GetString());
        Assert.True(row.TryGetProperty("paid_at", out _));

        var item = row.GetProperty("items")[0];
        Assert.Equal("Ribeye", item.GetProperty("dish_name").GetString());
        Assert.Equal(1, item.GetProperty("quantity").GetInt32());
        Assert.Equal(500m, item.GetProperty("unit_price").GetDecimal());

        var payment = row.GetProperty("payment_breakdowns")[0];
        Assert.Equal("EFECTIVO", payment.GetProperty("method").GetString());
        Assert.Equal(500m, payment.GetProperty("amount").GetDecimal());
    }

    private static async Task<PagedResponse<SalesHistoryItem>> GetAsync(SalesHistoryFilter filter)
    {
        await using var db = QueriesDatabase.CreateContext();
        return await new SalesHistoryService(db).GetAsync(filter);
    }
}
