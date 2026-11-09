using System.Text.Json.Serialization;
using MassTransit;

namespace PaymentsModule.Models;

// Fixed URN so routing doesn't depend on this namespace; Órdenes must publish with the same URN.
// JsonPropertyName keeps the wire contract agreed with Órdenes while the code stays in English.
[MessageUrn("DinningSessionReadyEvent")]
public record DinningSessionReadyEvent(
    Guid EventId,
    Guid DinningSessionId,
    [property: JsonPropertyName("mesaId")] Guid TableId,
    [property: JsonPropertyName("meseroId")] Guid WaiterId,
    DateTimeOffset Timestamp,
    IReadOnlyList<BillItem> Items,
    [property: JsonPropertyName("totales")] BillTotals Totals,
    [property: JsonPropertyName("nombreMesa")] string? TableName = null,
    [property: JsonPropertyName("orderFolio")] string? OrderFolio = null,
    [property: JsonPropertyName("nombreMesero")] string? WaiterName = null)
{
    [JsonPropertyName("order_folio")]
    public string? OrderFolioSnake { init => OrderFolio ??= value; }

    [JsonPropertyName("folioOrden")]
    public string? OrderFolioSpanish { init => OrderFolio ??= value; }

    [JsonPropertyName("table_name")]
    public string? TableNameSnake { init => TableName ??= value; }

    [JsonPropertyName("waiter_name")]
    public string? WaiterNameSnake { init => WaiterName ??= value; }
}

public record BillItem(
    [property: JsonPropertyName("platilloId")] Guid DishId,
    [property: JsonPropertyName("nombre")] string Name,
    [property: JsonPropertyName("cantidad")] int Quantity,
    [property: JsonPropertyName("precioUnitario")] decimal UnitPrice,
    [property: JsonPropertyName("importe")] decimal Amount,
    [property: JsonPropertyName("notas")] string? Notes = null)
{
    [JsonPropertyName("notes")]
    public string? NotesAlt { init => Notes ??= value; }
}

public record BillTotals(
    decimal Subtotal,
    [property: JsonPropertyName("impuestos")] decimal Taxes,
    [property: JsonPropertyName("totalEsperado")] decimal ExpectedTotal);
