using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

/// <summary>
/// Detail view of one payment transaction by folio for the FE detail modal (PMC-11 / PMC-25).
/// Contract agreed with the PO on 05/11/2026: English field names, no cancellation block.
/// </summary>
public record TransactionDetailResponse(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("cash_shift_id")] Guid CashShiftId,
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId,
    [property: JsonPropertyName("status")] TransactionStatus Status,
    [property: JsonPropertyName("paid_at")] DateTimeOffset PaidAt,
    [property: JsonPropertyName("subtotal")] decimal Subtotal,
    [property: JsonPropertyName("taxes")] decimal Taxes,
    [property: JsonPropertyName("tip")] decimal Tip,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("change_given")] decimal ChangeGiven,
    [property: JsonPropertyName("items")] IReadOnlyList<TransactionItemResponse> Items,
    [property: JsonPropertyName("payment_breakdowns")] IReadOnlyList<PaymentBreakdownResponse> Payments);

public record TransactionItemResponse(
    [property: JsonPropertyName("dish_name")] string DishName,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unit_price")] decimal UnitPrice,
    [property: JsonPropertyName("amount")] decimal Amount);

public record PaymentBreakdownResponse(
    [property: JsonPropertyName("method")] PaymentMethod Method,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("authorization_reference")] string? AuthorizationReference);
