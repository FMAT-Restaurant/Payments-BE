using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace PaymentsModule.Models;

/// <summary>
/// Query string parameters for paged sales history (PMC-10 / PMC-24).
/// Contract agreed with PO (05/11/2026): English parameters, semi-open date window [start_date, end_date).
/// </summary>
public class SalesHistoryQuery
{
    [FromQuery(Name = "start_date")]
    public DateTimeOffset? StartDate { get; set; }

    [FromQuery(Name = "fecha_inicio")]
    public DateTimeOffset? FechaInicio { set => StartDate ??= value; }

    [FromQuery(Name = "end_date")]
    public DateTimeOffset? EndDate { get; set; }

    [FromQuery(Name = "fecha_fin")]
    public DateTimeOffset? FechaFin { set => EndDate ??= value; }

    [FromQuery(Name = "page_index")]
    public int PageIndex { get; set; } = 1;

    [FromQuery(Name = "page_size")]
    public int PageSize { get; set; } = 10;

    /// <summary>Call only after <see cref="SalesHistoryQueryValidator"/> accepted the query.</summary>
    public SalesHistoryFilter ToFilter() => new(
        StartDate,
        EndDate,
        PageIndex,
        PageSize);
}

public record SalesHistoryFilter(
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
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
    [property: JsonPropertyName("cash_shift_id")] Guid CashShiftId,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("status")] TransactionStatus Status,
    [property: JsonPropertyName("paid_at")] DateTimeOffset PaidAt,
    [property: JsonPropertyName("items")] IReadOnlyList<SalesHistoryLine> Items,
    [property: JsonPropertyName("payment_breakdowns")] IReadOnlyList<SalesHistoryPaymentBreakdown> Payments);

public record SalesHistoryLine(
    [property: JsonPropertyName("dish_name")] string DishName,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unit_price")] decimal UnitPrice);

public record SalesHistoryPaymentBreakdown(
    [property: JsonPropertyName("method")] PaymentMethod Method,
    [property: JsonPropertyName("amount")] decimal Amount);
