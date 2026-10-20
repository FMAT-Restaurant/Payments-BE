using System.Text.Json.Serialization;

namespace PaymentsModule.Models.Dtos;

public sealed record AbrirCajaRequest
{
    [JsonPropertyName("fondo_inicial")]
    public decimal? FondoInicial { get; init; }
}

public sealed record CerrarCajaRequest
{
    [JsonPropertyName("efectivo_declarado")]
    public decimal? EfectivoDeclarado { get; init; }
}

public sealed record AbrirCajaResponseDTO(
    [property: JsonPropertyName("turno_caja_id")] Guid TurnoCajaId,
    [property: JsonPropertyName("estado")] string Estado,
    [property: JsonPropertyName("mensaje")] string Mensaje);

public sealed record CerrarCajaResponseDTO(
    [property: JsonPropertyName("turno_caja_id")] Guid TurnoCajaId,
    [property: JsonPropertyName("estado")] string Estado,
    [property: JsonPropertyName("diferencia")] decimal Diferencia,
    [property: JsonPropertyName("mensaje")] string Mensaje);
