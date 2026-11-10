using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

// Wire contract of PMC-6/PMC-7 (05/11/2026): English snake_case. Items are not sent: the bill comes from Redis (PMC-5).
public record ChargeRequest(
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId,
    [property: JsonPropertyName("tip")] decimal Tip,
    [property: JsonPropertyName("payments")] IReadOnlyList<PaymentRequest> Payments);

public record PaymentRequest(
    [property: JsonPropertyName("method")] PaymentMethod Method,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("authorization_reference")] string? AuthorizationReference);

// Field names as written in PMC-13: `estado` and `mensaje` stay in Spanish there.
public record ChargeResponse(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] TransactionStatus Status,
    [property: JsonPropertyName("change_given")] decimal ChangeGiven,
    [property: JsonPropertyName("mensaje")] string Message);
