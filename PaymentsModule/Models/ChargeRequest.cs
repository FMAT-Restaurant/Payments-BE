using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

// Wire contract with the frontend / API Gateway keeps the Spanish snake_case names from PMC-6.
public record ChargeRequest(
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId,
    [property: JsonPropertyName("propina")] decimal Tip,
    [property: JsonPropertyName("pagos")] IReadOnlyList<PaymentRequest> Payments,
    [property: JsonPropertyName("detalles_snapshot")] IReadOnlyList<ItemSnapshot> ItemsSnapshot);

public record PaymentRequest(
    [property: JsonPropertyName("metodo_pago")] PaymentMethod Method,
    [property: JsonPropertyName("monto")] decimal Amount,
    [property: JsonPropertyName("referencia_autorizacion")] string? AuthorizationReference);

public record ItemSnapshot(
    [property: JsonPropertyName("nombre_platillo")] string DishName,
    [property: JsonPropertyName("cantidad")] int Quantity,
    [property: JsonPropertyName("precio_unitario")] decimal UnitPrice,
    [property: JsonPropertyName("importe")] decimal Amount);

public record ChargeResponse(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] TransactionStatus Status,
    [property: JsonPropertyName("cambio_entregado")] decimal ChangeGiven,
    [property: JsonPropertyName("mensaje")] string Message);
