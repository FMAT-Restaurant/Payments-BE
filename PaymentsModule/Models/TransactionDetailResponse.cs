using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

// Ticket view of one transaction. Spanish snake_case names are the wire contract of PMC-11.
public record TransactionDetailResponse(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("turno_caja_id")] Guid CashShiftId,
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] TransactionStatus Status,
    [property: JsonPropertyName("detalles")] IReadOnlyList<TransactionItemResponse> Items,
    [property: JsonPropertyName("desgloses")] IReadOnlyList<PaymentBreakdownResponse> Payments);

public record TransactionItemResponse(
    [property: JsonPropertyName("nombre_platillo")] string DishName,
    [property: JsonPropertyName("cantidad")] int Quantity,
    [property: JsonPropertyName("precio_unitario")] decimal UnitPrice,
    [property: JsonPropertyName("importe")] decimal Amount);

public record PaymentBreakdownResponse(
    [property: JsonPropertyName("metodo_pago")] PaymentMethod Method,
    [property: JsonPropertyName("monto")] decimal Amount,
    [property: JsonPropertyName("referencia_autorizacion")] string? AuthorizationReference);
