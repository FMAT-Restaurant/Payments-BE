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

        var missing = Assert.IsType<ObjectResult>(await controller.GetByFolio("TX-99999", CancellationToken.None));
        var found = Assert.IsType<OkObjectResult>(await controller.GetByFolio("TX-20260901-000001", CancellationToken.None));

        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
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
        Assert.Equal("COMPLETADO", root.GetProperty("estado").GetString());
        Assert.Equal(750m, root.GetProperty("total").GetDecimal());
        Assert.Equal(750m, root.GetProperty("detalles")[0].GetProperty("precio_unitario").GetDecimal());
        var payment = root.GetProperty("desgloses")[0];
        Assert.Equal("TARJETA_CREDITO", payment.GetProperty("metodo_pago").GetString());
        Assert.Equal(750m, payment.GetProperty("monto").GetDecimal());
        Assert.Equal("AUTH-123456", payment.GetProperty("referencia_autorizacion").GetString());
    }

    private static async Task<TransactionDetailResponse?> GetAsync(string folio)
    {
        await using var db = QueriesDatabase.CreateContext();
        return await new TransactionDetailService(db).GetByFolioAsync(folio);
    }
}
