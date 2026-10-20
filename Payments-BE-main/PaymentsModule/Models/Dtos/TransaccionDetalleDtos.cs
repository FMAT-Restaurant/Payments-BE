using System.Text.Json.Serialization;

namespace PaymentsModule.Models.Dtos;

/// <summary>Vista en detalle (ticket) de una transacción. Los montos son decimal (RNF-05).</summary>
public sealed record TransaccionDetalleDTO(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("turno_caja_id")] Guid TurnoCajaId,
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] string Estado,
    [property: JsonPropertyName("detalles")] IReadOnlyList<DetalleTransaccionDTO> Detalles,
    [property: JsonPropertyName("desgloses")] IReadOnlyList<DesglosePagoDTO> Desgloses);

public sealed record DetalleTransaccionDTO(
    [property: JsonPropertyName("nombre_platillo")] string NombrePlatillo,
    [property: JsonPropertyName("cantidad")] int Cantidad,
    [property: JsonPropertyName("precio_unitario")] decimal PrecioUnitario,
    [property: JsonPropertyName("importe")] decimal Importe);

public sealed record DesglosePagoDTO(
    [property: JsonPropertyName("metodo_pago")] string MetodoPago,
    [property: JsonPropertyName("monto")] decimal Monto,
    [property: JsonPropertyName("referencia_autorizacion")] string? ReferenciaAutorizacion);
