using System.Text.Json.Serialization;
using PaymentsModule.Models.Entities;

namespace PaymentsModule.Models.Dtos;

public sealed record CobroRequestDTO
{
    [JsonPropertyName("dinning_session_id")]
    public Guid DinningSessionId { get; init; }

    [JsonPropertyName("detalles_snapshot")]
    public List<DetalleSnapshotDTO> DetallesSnapshot { get; init; } = [];

    [JsonPropertyName("pagos")]
    public List<PagoDTO> Pagos { get; init; } = [];
}

public sealed record DetalleSnapshotDTO
{
    [JsonPropertyName("nombre_platillo")]
    public string NombrePlatillo { get; init; } = string.Empty;

    [JsonPropertyName("cantidad")]
    public int Cantidad { get; init; }

    [JsonPropertyName("precio_unitario")]
    public decimal PrecioUnitario { get; init; }

    [JsonPropertyName("importe")]
    public decimal Importe { get; init; }
}

public sealed record PagoDTO
{
    [JsonPropertyName("metodo_pago")]
    public MetodoPago MetodoPago { get; init; }

    [JsonPropertyName("monto")]
    public decimal Monto { get; init; }

    [JsonPropertyName("referencia_autorizacion")]
    public string? ReferenciaAutorizacion { get; init; }
}

public sealed record CobroResponseDTO(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] string Estado,
    [property: JsonPropertyName("mensaje")] string Mensaje);
