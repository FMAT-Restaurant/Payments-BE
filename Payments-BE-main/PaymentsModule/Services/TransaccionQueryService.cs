using Microsoft.EntityFrameworkCore;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Persistence;

namespace PaymentsModule.Services;

public sealed class TransaccionQueryService(PaymentsDbContext db) : ITransaccionQueryService
{
    public async Task<TransaccionDetalleDTO?> ObtenerPorFolioAsync(string folio, CancellationToken ct)
    {
        // Una sola consulta (cabecera + detalles + desgloses), solo lectura y sin Change Tracker.
        // No se abre transacción explícita: se lee con el aislamiento por defecto (Read Committed).
        var transaccion = await db.Transacciones
            .AsNoTracking()
            .Include(t => t.Detalles)
            .Include(t => t.Desgloses)
            .FirstOrDefaultAsync(t => t.Folio == folio, ct);

        return transaccion?.ToDetalleDto();
    }

    public async Task<PagedResultDTO<HistorialItemDTO>> ObtenerHistorialAsync(HistorialQuery query, CancellationToken ct)
    {
        var filtrada = AplicarFiltros(db.Transacciones.AsNoTracking(), query);

        var totalCount = await filtrada.CountAsync(ct);

        // Proyección selectiva: EF Core traduce el Select a un SELECT con solo las columnas necesarias.
        // Todo sale de las tablas propias (snapshot); no hay uniones con catálogos externos.
        var data = await filtrada
            .OrderByDescending(t => t.FechaCreacionUtc)
            .ThenByDescending(t => t.Id)
            .Skip((query.PageIndex - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new HistorialItemDTO(
                t.Folio,
                t.Total,
                t.Estado,
                t.FechaCreacionUtc,
                t.Detalles
                    .Select(d => new HistorialDetalleDTO(d.NombrePlatillo, d.Cantidad, d.PrecioUnitario))
                    .ToList()))
            .ToListAsync(ct);

        var totalPages = (totalCount + query.PageSize - 1) / query.PageSize;

        return new PagedResultDTO<HistorialItemDTO>(query.PageIndex, query.PageSize, totalCount, totalPages, data);
    }

    private static IQueryable<TransaccionPago> AplicarFiltros(IQueryable<TransaccionPago> origen, HistorialQuery query)
    {
        var resultado = origen;

        if (query.FechaInicio is { } inicio)
        {
            var desde = inicio.UtcDateTime;
            resultado = resultado.Where(t => t.FechaCreacionUtc >= desde);
        }

        if (query.FechaFin is { } fin)
        {
            var hasta = fin.UtcDateTime;
            resultado = resultado.Where(t => t.FechaCreacionUtc <= hasta);
        }

        if (query.Estado is { } estado)
        {
            resultado = resultado.Where(t => t.Estado == estado);
        }

        if (query.MetodoPago is { } metodo)
        {
            resultado = resultado.Where(t => t.Desgloses.Any(d => d.MetodoPago == metodo));
        }

        return resultado;
    }
}
