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

        var first = await GetAsync(new SalesHistoryFilter(null, null, null, null, 1, 10));
        var second = await GetAsync(new SalesHistoryFilter(null, null, null, null, 2, 10));

        Assert.Equal((1, 10, 12, 2), (first.PageIndex, first.PageSize, first.TotalCount, first.TotalPages));
        Assert.Equal(10, first.Data.Count);
        Assert.Equal("TX-20260901-000012", first.Data[0].Folio);
        Assert.Equal(2, second.Data.Count);
        Assert.Equal("TX-20260901-000001", second.Data[^1].Folio);
    }

    [Fact]
    public async Task Projects_the_snapshot_lines_with_decimal_prices()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Start, TransactionStatus.Completed, (PaymentMethod.Cash, 500m));

        var page = await GetAsync(new SalesHistoryFilter(null, null, null, null, 1, 10));

        var row = Assert.Single(page.Data);
        Assert.Equal((500m, Start), (row.Total, row.ChargedAt));
        var line = Assert.Single(row.Items);
        Assert.Equal(("Ribeye", 1, 500m), (line.DishName, line.Quantity, line.UnitPrice));
    }

    [Fact]
    public async Task Applies_only_the_filters_that_are_present()
    {
        await QueriesDatabase.SeedAsync("TX-OLD-CASH", Start.AddDays(-30), TransactionStatus.Completed, (PaymentMethod.Cash, 100m));
        await QueriesDatabase.SeedAsync("TX-NEW-CASH", Start, TransactionStatus.Completed, (PaymentMethod.Cash, 100m));
        await QueriesDatabase.SeedAsync("TX-NEW-CARD", Start, TransactionStatus.Completed, (PaymentMethod.CreditCard, 100m));
        await QueriesDatabase.SeedAsync("TX-NEW-CANCELLED", Start, TransactionStatus.Cancelled, (PaymentMethod.Cash, 100m));

        var byStatus = await GetAsync(new SalesHistoryFilter(null, null, TransactionStatus.Cancelled, null, 1, 10));
        var byMethod = await GetAsync(new SalesHistoryFilter(null, null, null, PaymentMethod.CreditCard, 1, 10));
        var byRange = await GetAsync(new SalesHistoryFilter(Start.AddDays(-1), Start.AddDays(1), null, null, 1, 10));
        var combined = await GetAsync(new SalesHistoryFilter(Start.AddDays(-1), null, TransactionStatus.Completed, PaymentMethod.Cash, 1, 10));
        var unfiltered = await GetAsync(new SalesHistoryFilter(null, null, null, null, 1, 10));

        Assert.Equal("TX-NEW-CANCELLED", Assert.Single(byStatus.Data).Folio);
        Assert.Equal("TX-NEW-CARD", Assert.Single(byMethod.Data).Folio);
        Assert.Equal(3, byRange.TotalCount);
        Assert.Equal("TX-NEW-CASH", Assert.Single(combined.Data).Folio);
        Assert.Equal(4, unfiltered.TotalCount);
    }

    [Fact]
    public async Task Returns_an_empty_page_when_nothing_matches()
    {
        var page = await GetAsync(new SalesHistoryFilter(null, null, null, null, 1, 10));

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
            new SalesHistoryQuery { From = Start.AddDays(1), To = Start }, CancellationToken.None));
        var valid = Assert.IsType<OkObjectResult>(await controller.Get(
            new SalesHistoryQuery { Status = "COMPLETADO" }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.Equal(1, Assert.IsType<PagedResponse<SalesHistoryItem>>(valid.Value).TotalCount);
    }

    [Fact]
    public async Task Serializes_the_wire_contract()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Start, TransactionStatus.Completed, (PaymentMethod.Cash, 500m));

        var page = await GetAsync(new SalesHistoryFilter(null, null, null, null, 1, 10));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        var root = json.RootElement;
        Assert.Equal((1, 10, 1, 1), (
            root.GetProperty("page_index").GetInt32(),
            root.GetProperty("page_size").GetInt32(),
            root.GetProperty("total_count").GetInt32(),
            root.GetProperty("total_pages").GetInt32()));
        var row = root.GetProperty("data")[0];
        Assert.Equal("COMPLETADO", row.GetProperty("estado").GetString());
        Assert.Equal(500m, row.GetProperty("total").GetDecimal());
        Assert.True(row.TryGetProperty("fecha_cobro", out _));
        Assert.Equal("Ribeye", row.GetProperty("detalles")[0].GetProperty("nombre_platillo").GetString());
    }

    private static async Task<PagedResponse<SalesHistoryItem>> GetAsync(SalesHistoryFilter filter)
    {
        await using var db = QueriesDatabase.CreateContext();
        return await new SalesHistoryService(db).GetAsync(filter);
    }
}
