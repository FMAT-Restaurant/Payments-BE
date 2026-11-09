using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Controllers;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// Runs against a real SQL Server (see QueriesDatabase).
[Collection(QueriesDatabase.CollectionName)]
public class TransactionDetailTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => await QueriesDatabase.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Returns_header_items_and_payments_from_the_stored_snapshot()
    {
        var saved = await QueriesDatabase.SeedAsync(
            "TX-20260901-000001", Moment, TransactionStatus.Completed, (PaymentMethod.Cash, 200m), (PaymentMethod.CreditCard, 300m));

        var detail = await GetAsync("TX-20260901-000001");

        Assert.NotNull(detail);
        Assert.Equal((saved.CashShiftId, saved.DinningSessionId), (detail.CashShiftId, detail.DinningSessionId));
        Assert.Equal((500m, TransactionStatus.Completed), (detail.Total, detail.Status));
        Assert.Equal(saved.CreatedAt, detail.PaidAt);
        Assert.Equal(saved.Subtotal, detail.Subtotal);
        Assert.Equal(saved.Taxes, detail.Taxes);
        Assert.Equal(saved.Tip, detail.Tip);
        Assert.Equal(0.00m, detail.ChangeGiven);
        var item = Assert.Single(detail.Items);
        Assert.Equal(("Ribeye", 1, 500m, 500m), (item.DishName, item.Quantity, item.UnitPrice, item.Amount));
        Assert.Equal(2, detail.Payments.Count);
        Assert.Contains(detail.Payments, p => p is { Method: PaymentMethod.CreditCard, Amount: 300m, AuthorizationReference: "AUTH-123456" });
    }

    [Fact]
    public async Task Returns_null_when_the_folio_does_not_exist()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Moment);

        Assert.Null(await GetAsync("TX-99999"));
    }

    [Fact]
    public async Task Controller_answers_404_for_an_unknown_folio_and_200_for_a_known_one()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Moment);
        await using var db = QueriesDatabase.CreateContext();
        var controller = new TransactionDetailController(new TransactionDetailService(db));

        var missing = Assert.IsType<NotFoundObjectResult>(await controller.GetByFolio("TX-99999", CancellationToken.None));
        var found = Assert.IsType<OkObjectResult>(await controller.GetByFolio("TX-20260901-000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        var error = Assert.IsType<ErrorResponse>(missing.Value);
        Assert.Equal("TRANSACTION_NOT_FOUND", error.Code);
        Assert.Equal("El ticket 'TX-99999' no existe", error.Message);
        Assert.IsType<TransactionDetailResponse>(found.Value);
    }

    [Fact]
    public async Task Serializes_the_wire_contract_with_decimal_amounts()
    {
        await QueriesDatabase.SeedAsync("TX-20260901-000001", Moment, TransactionStatus.Completed, (PaymentMethod.CreditCard, 750m));

        var detail = await GetAsync("TX-20260901-000001");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail));
        var root = json.RootElement;
        Assert.Equal("TX-20260901-000001", root.GetProperty("folio").GetString());
        Assert.Equal("COMPLETADO", root.GetProperty("status").GetString());
        Assert.Equal(750m, root.GetProperty("total").GetDecimal());
        Assert.Equal(750m, root.GetProperty("subtotal").GetDecimal());
        Assert.Equal(0m, root.GetProperty("taxes").GetDecimal());
        Assert.Equal(0m, root.GetProperty("tip").GetDecimal());
        Assert.Equal(0m, root.GetProperty("change_given").GetDecimal());
        Assert.Equal("Ribeye", root.GetProperty("items")[0].GetProperty("dish_name").GetString());
        Assert.Equal(1, root.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(750m, root.GetProperty("items")[0].GetProperty("unit_price").GetDecimal());
        Assert.Equal(750m, root.GetProperty("items")[0].GetProperty("amount").GetDecimal());
        var payment = root.GetProperty("payment_breakdowns")[0];
        Assert.Equal("TARJETA_CREDITO", payment.GetProperty("method").GetString());
        Assert.Equal(750m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal("AUTH-123456", payment.GetProperty("authorization_reference").GetString());
    }

    private static async Task<TransactionDetailResponse?> GetAsync(string folio)
    {
        await using var db = QueriesDatabase.CreateContext();
        return await new TransactionDetailService(db).GetByFolioAsync(folio);
    }
}
