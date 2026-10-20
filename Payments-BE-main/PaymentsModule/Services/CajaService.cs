using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Persistence;

namespace PaymentsModule.Services;

public sealed class CajaService(PaymentsDbContext db, TimeProvider timeProvider, ILogger<CajaService> logger) : ICajaService
{
    private const string MensajeTurnoYaAbierto = "El cajero ya tiene un turno abierto";
    private const string MensajeSinTurno = "El cajero no tiene un turno abierto";
    private const string MensajeConflicto = "El turno fue modificado por otra operación, intente de nuevo";

    public async Task<CajaResult<AbrirCajaResponseDTO>> AbrirAsync(decimal fondoInicial, string cajeroId, CancellationToken ct)
    {
        var yaAbierto = await db.TurnosCaja.AnyAsync(t => t.CajeroId == cajeroId && t.Estado == EstadoTurno.ABIERTO, ct);
        if (yaAbierto)
        {
            return CajaResult<AbrirCajaResponseDTO>.Falla(CajaError.TurnoYaAbierto, MensajeTurnoYaAbierto);
        }

        var turno = new TurnoCaja
        {
            Id = Guid.CreateVersion7(),
            CajeroId = cajeroId,
            Estado = EstadoTurno.ABIERTO,
            FondoInicial = fondoInicial,
            FechaAperturaUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        db.TurnosCaja.Add(turno);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EsTurnoAbiertoDuplicado(ex))
        {
            // Doble clic / peticiones simultáneas: el índice único filtrado detiene a la segunda.
            logger.LogWarning("Apertura de turno duplicada para el cajero {CajeroId}", cajeroId);
            return CajaResult<AbrirCajaResponseDTO>.Falla(CajaError.TurnoYaAbierto, MensajeTurnoYaAbierto);
        }

        return CajaResult<AbrirCajaResponseDTO>.Exito(
            new AbrirCajaResponseDTO(turno.Id, turno.Estado.ToString(), "Turno abierto exitosamente"));
    }

    public async Task<CajaResult<CerrarCajaResponseDTO>> CerrarAsync(decimal efectivoDeclarado, string cajeroId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // Bloquea la fila del turno: los cobros en curso terminan antes y los nuevos ven el turno ya cerrado.
            var abierto = EstadoTurno.ABIERTO.ToString();
            var turnos = await db.TurnosCaja
                .FromSqlInterpolated($"SELECT * FROM TurnoCaja WITH (UPDLOCK, ROWLOCK) WHERE CajeroId = {cajeroId} AND Estado = {abierto}")
                .ToListAsync(ct);

            var turno = turnos.FirstOrDefault();
            if (turno is null)
            {
                return CajaResult<CerrarCajaResponseDTO>.Falla(CajaError.SinTurnoAbierto, MensajeSinTurno);
            }

            // Ventas completadas del turno agrupadas por método de pago (el efectivo ya viene neto de cambio).
            var totales = await db.Transacciones
                .AsNoTracking()
                .Where(t => t.TurnoCajaId == turno.Id && t.Estado == EstadoTransaccion.COMPLETADO)
                .SelectMany(t => t.Desgloses)
                .GroupBy(d => d.MetodoPago)
                .Select(g => new { Metodo = g.Key, Total = g.Sum(d => d.Monto) })
                .ToListAsync(ct);

            var ventasEfectivo = totales.Where(x => x.Metodo == MetodoPago.EFECTIVO).Sum(x => x.Total);
            var ventasTarjeta = totales.Where(x => x.Metodo == MetodoPago.TARJETA_CREDITO).Sum(x => x.Total);

            var cuadre = CajaCalculator.Cuadrar(turno.FondoInicial, ventasEfectivo, efectivoDeclarado);

            turno.Estado = cuadre.Estado;
            turno.FechaCierreUtc = timeProvider.GetUtcNow().UtcDateTime;
            turno.EfectivoEsperado = cuadre.EfectivoEsperado;
            turno.EfectivoDeclarado = efectivoDeclarado;
            turno.Diferencia = cuadre.Diferencia;
            turno.VentasTarjeta = ventasTarjeta;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return CajaResult<CerrarCajaResponseDTO>.Exito(
                new CerrarCajaResponseDTO(turno.Id, turno.Estado.ToString(), cuadre.Diferencia, cuadre.Mensaje));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(ex, "Conflicto de concurrencia al cerrar el turno del cajero {CajeroId}", cajeroId);
            return CajaResult<CerrarCajaResponseDTO>.Falla(CajaError.ConflictoConcurrencia, MensajeConflicto);
        }
        catch
        {
            // Cualquier otro fallo (p. ej. timeout): ROLLBACK, el turno sigue ABIERTO y el manejador global responde 500.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool EsTurnoAbiertoDuplicado(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains(PaymentsDbContext.IndiceTurnoAbiertoUnico, StringComparison.Ordinal);
}
