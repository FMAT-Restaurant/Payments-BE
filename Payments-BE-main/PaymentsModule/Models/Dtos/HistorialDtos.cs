using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models.Entities;

namespace PaymentsModule.Models.Dtos;

/// <summary>Parámetros de consulta del historial de ventas (query string).</summary>
public sealed class HistorialQuery
{
    [FromQuery(Name = "fecha_inicio")]
    public DateTimeOffset? FechaInicio { get; set; }

    [FromQuery(Name = "fecha_fin")]
    public DateTimeOffset? FechaFin { get; set; }

    [FromQuery(Name = "estado")]
    public EstadoTransaccion? Estado { get; set; }

    [FromQuery(Name = "metodo_pago")]
    public MetodoPago? MetodoPago { get; set; }

    [FromQuery(Name = "page_index")]
    public int PageIndex { get; set; } = 1;

    [FromQuery(Name = "page_size")]
    public int PageSize { get; set; } = 10;
}

public sealed record PagedResultDTO<T>(
    [property: JsonPropertyName("page_index")] int PageIndex,
    [property: JsonPropertyName("page_size")] int PageSize,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data);

public sealed record HistorialItemDTO(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("estado")] EstadoTransaccion Estado,
    [property: JsonPropertyName("fecha_cobro")] DateTime FechaCobro,
    [property: JsonPropertyName("detalles")] IReadOnlyList<HistorialDetalleDTO> Detalles);

public sealed record HistorialDetalleDTO(
    [property: JsonPropertyName("nombre_platillo")] string NombrePlatillo,
    [property: JsonPropertyName("cantidad")] int Cantidad,
    [property: JsonPropertyName("precio_unitario")] decimal PrecioUnitario);
