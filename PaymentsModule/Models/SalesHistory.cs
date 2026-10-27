using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace PaymentsModule.Models;

// Query string of the sales history. Spanish snake_case names are the wire contract of PMC-10.
public class SalesHistoryQuery
{
    [FromQuery(Name = "fecha_inicio")]
    public DateTimeOffset? From { get; set; }

    [FromQuery(Name = "fecha_fin")]
    public DateTimeOffset? To { get; set; }

    // Enum filters arrive as wire names (COMPLETADO, EFECTIVO, ...); the validator checks them and ToFilter() parses them.
    [FromQuery(Name = "estado")]
    public string? Status { get; set; }

    [FromQuery(Name = "metodo_pago")]
    public string? Method { get; set; }

    [FromQuery(Name = "page_index")]
    public int PageIndex { get; set; } = 1;

    [FromQuery(Name = "page_size")]
    public int PageSize { get; set; } = 10;

    /// <summary>Call only after <see cref="SalesHistoryQueryValidator"/> accepted the query.</summary>
    public SalesHistoryFilter ToFilter() => new(
        From,
        To,
        WireEnum.TryParse<TransactionStatus>(Status, out var status) ? status : null,
        WireEnum.TryParse<PaymentMethod>(Method, out var method) ? method : null,
        PageIndex,
        PageSize);
}

public record SalesHistoryFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    TransactionStatus? Status,
    PaymentMethod? Method,
    int PageIndex,
    int PageSize);

public record PagedResponse<T>(
    [property: JsonPropertyName("page_index")] int PageIndex,
    [property: JsonPropertyName("page_size")] int PageSize,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data);

public record SalesHistoryItem(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] TransactionStatus Status,
    [property: JsonPropertyName("fecha_cobro")] DateTimeOffset ChargedAt,
    [property: JsonPropertyName("detalles")] List<SalesHistoryLine> Items);

public record SalesHistoryLine(
    [property: JsonPropertyName("nombre_platillo")] string DishName,
    [property: JsonPropertyName("cantidad")] int Quantity,
    [property: JsonPropertyName("precio_unitario")] decimal UnitPrice);
