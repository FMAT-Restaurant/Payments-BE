using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

/// <summary>
/// DTO for pending dining sessions in the cashier inbox and screen reload (HU-FE-02 / PMC-5).
/// Contract strictly in English and snake_case.
/// </summary>
public record PendingBillResponse(
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId,
    [property: JsonPropertyName("order_folio")] string? OrderFolio,
    [property: JsonPropertyName("table_name")] string? TableName,
    [property: JsonPropertyName("waiter_name")] string? WaiterName,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("items")] IReadOnlyList<PendingBillItemResponse> Items,
    [property: JsonPropertyName("subtotal")] decimal Subtotal,
    [property: JsonPropertyName("taxes")] decimal Taxes,
    [property: JsonPropertyName("total")] decimal Total)
{
    public static PendingBillResponse FromEvent(DinningSessionReadyEvent e)
    {
        var orderFolio = !string.IsNullOrWhiteSpace(e.OrderFolio) ? e.OrderFolio : null;
        var tableName = !string.IsNullOrWhiteSpace(e.TableName) ? e.TableName : null;
        var waiterName = !string.IsNullOrWhiteSpace(e.WaiterName) ? e.WaiterName : null;

        return new PendingBillResponse(
            e.DinningSessionId,
            orderFolio,
            tableName,
            waiterName,
            e.Timestamp,
            e.Items.Select(i => new PendingBillItemResponse(
                i.DishId,
                i.Name,
                i.Quantity,
                i.UnitPrice,
                i.Amount,
                i.Notes)).ToList(),
            e.Totals.Subtotal,
            e.Totals.Taxes,
            e.Totals.ExpectedTotal);
    }
}

public record PendingBillItemResponse(
    [property: JsonPropertyName("dish_id")] Guid DishId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unit_price")] decimal UnitPrice,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("notes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Notes);
