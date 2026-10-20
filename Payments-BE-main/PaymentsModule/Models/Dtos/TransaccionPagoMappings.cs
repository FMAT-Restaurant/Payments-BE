using PaymentsModule.Models.Entities;

namespace PaymentsModule.Models.Dtos;

/// <summary>
/// Proyección explícita entidad -> DTO. Evita referencias circulares al serializar y
/// solo usa los datos snapshot guardados en la compra (sin catálogos externos).
/// </summary>
public static class TransaccionPagoMappings
{
    public static TransaccionDetalleDTO ToDetalleDto(this TransaccionPago t) => new(
        t.Folio,
        t.TurnoCajaId,
        t.DinningSessionId,
        t.Total,
        t.Estado.ToString(),
        t.Detalles
            .Select(d => new DetalleTransaccionDTO(d.NombrePlatillo, d.Cantidad, d.PrecioUnitario, d.Importe))
            .ToList(),
        t.Desgloses
            .Select(d => new DesglosePagoDTO(d.MetodoPago.ToString(), d.Monto, d.ReferenciaAutorizacion))
            .ToList());
}
