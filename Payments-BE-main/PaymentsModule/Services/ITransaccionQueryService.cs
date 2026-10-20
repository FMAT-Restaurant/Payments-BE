using PaymentsModule.Models.Dtos;

namespace PaymentsModule.Services;

public interface ITransaccionQueryService
{
    /// <summary>Devuelve el detalle del folio, o <c>null</c> si no existe.</summary>
    Task<TransaccionDetalleDTO?> ObtenerPorFolioAsync(string folio, CancellationToken ct);

    /// <summary>Historial de ventas paginado con filtros opcionales.</summary>
    Task<PagedResultDTO<HistorialItemDTO>> ObtenerHistorialAsync(HistorialQuery query, CancellationToken ct);
}
