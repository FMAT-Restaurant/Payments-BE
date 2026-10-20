using System.Data;
using System.Globalization;
using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Models.Events;
using PaymentsModule.Persistence;
using PaymentsModule.Services.Gateway;

namespace PaymentsModule.Services;

public sealed class TurnoCajaNoAbiertoException : Exception;

public sealed class CobroService(
    PaymentsDbContext db,
    IPublishEndpoint publishEndpoint,
    IPaymentGatewayClient gateway,
    IOptions<CobroOptions> options,
    TimeProvider timeProvider,
    ILogger<CobroService> logger) : ICobroService
{
    public async Task<CobroResult> ProcesarCobroAsync(CobroRequestDTO request, string cajeroId, CancellationToken ct)
    {
        // 1. Validación matemática (sin tocar BD ni pasarela).
        var total = request.DetallesSnapshot.Sum(d => d.Importe);
        var liquidacion = CobroCalculator.Liquidar(total, request.Pagos);

        switch (liquidacion.Estado)
        {
            case EstadoLiquidacion.FondosInsuficientes:
                return CobroResult.Falla(CobroError.FondosInsuficientes, CobroMensajes.FondosInsuficientes);
            case EstadoLiquidacion.ExcedenteNoAbsorbible:
                return CobroResult.Falla(CobroError.ExcedenteNoAbsorbible, CobroMensajes.ExcedenteNoAbsorbible);
        }

        // 2. Turno de caja activo del cajero (identidad proveniente del JWT).
        var turno = await db.TurnosCaja
            .AsNoTracking()
            .Where(t => t.CajeroId == cajeroId && t.Estado == EstadoTurno.ABIERTO)
            .OrderByDescending(t => t.FechaAperturaUtc)
            .FirstOrDefaultAsync(ct);

        if (turno is null)
        {
            return CobroResult.Falla(CobroError.TurnoCajaInactivo, CobroMensajes.TurnoCajaInactivo);
        }

        // 3. Idempotencia: la cuenta ya fue pagada.
        var yaPagada = await db.Transacciones.AnyAsync(
            t => t.DinningSessionId == request.DinningSessionId && t.Estado == EstadoTransaccion.COMPLETADO, ct);

        if (yaPagada)
        {
            return CobroResult.Falla(CobroError.YaPagado, CobroMensajes.YaPagado);
        }

        // 4. Cargos con tarjeta en la pasarela externa (antes de abrir la transacción de BD).
        var cargos = new List<GatewayChargeRequest>();
        foreach (var pago in liquidacion.Pagos.Where(p => p.Metodo == MetodoPago.TARJETA_CREDITO))
        {
            var cargo = new GatewayChargeRequest(request.DinningSessionId, pago.Referencia!, pago.Monto);

            try
            {
                var resultado = await gateway.ChargeAsync(cargo, ct);
                if (!resultado.Approved)
                {
                    logger.LogWarning("Tarjeta rechazada para la sesión {SesionId}: {Motivo}", request.DinningSessionId, resultado.DeclineReason);
                    await RevertirCargosAsync(cargos);
                    return CobroResult.Falla(CobroError.TarjetaRechazada, CobroMensajes.TarjetaRechazada);
                }

                cargos.Add(cargo);
            }
            catch (PaymentGatewayException ex)
            {
                logger.LogError(ex, "Pasarela no disponible para la sesión {SesionId}", request.DinningSessionId);

                // Estado desconocido: el cargo pudo aplicarse aunque no llegó la respuesta; se intenta extornar también.
                cargos.Add(cargo);
                await RevertirCargosAsync(cargos);
                return CobroResult.Falla(CobroError.PasarelaNoDisponible, CobroMensajes.PasarelaNoDisponible);
            }
            catch (OperationCanceledException)
            {
                await RevertirCargosAsync([.. cargos, cargo]);
                throw;
            }
        }

        // 5. Persistencia atómica; si algo falla, se extornan los cargos de tarjeta.
        try
        {
            var respuesta = await PersistirAsync(request, liquidacion, turno.Id);
            return CobroResult.Exito(respuesta);
        }
        catch (DbUpdateException ex) when (EsDuplicadoDeSesion(ex))
        {
            // Carrera entre dos peticiones idénticas: la otra ganó el COMMIT.
            logger.LogWarning("Cobro duplicado concurrente para la sesión {SesionId}", request.DinningSessionId);
            await RevertirCargosNoPersistidosAsync(request.DinningSessionId, cargos);
            return CobroResult.Falla(CobroError.YaPagado, CobroMensajes.YaPagado);
        }
        catch (TurnoCajaNoAbiertoException)
        {
            // El turno se cerró entre la validación y el COMMIT: se extornan los cargos de tarjeta.
            logger.LogWarning("El turno {TurnoId} ya no está abierto; se aborta el cobro de la sesión {SesionId}", turno.Id, request.DinningSessionId);
            await RevertirCargosAsync(cargos);
            return CobroResult.Falla(CobroError.TurnoCajaInactivo, CobroMensajes.TurnoCajaInactivo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo al persistir el cobro de la sesión {SesionId}; se revierte en la pasarela", request.DinningSessionId);
            await RevertirCargosAsync(cargos);
            return CobroResult.Falla(CobroError.ErrorInterno, CobroMensajes.ErrorInterno);
        }
    }

    private async Task<CobroResponseDTO> PersistirAsync(CobroRequestDTO request, Liquidacion liquidacion, Guid turnoCajaId)
    {
        // Se usa CancellationToken.None a propósito: una vez cobrada la tarjeta, la cancelación del cliente
        // no debe dejar la operación a medias.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        try
        {
            // Bloquea la fila del turno hasta el COMMIT: serializa el cobro contra el cierre de caja,
            // de modo que ninguna venta se cuele en un turno ya cuadrado.
            var turnos = await db.TurnosCaja
                .FromSqlInterpolated($"SELECT * FROM TurnoCaja WITH (UPDLOCK, ROWLOCK) WHERE Id = {turnoCajaId}")
                .AsNoTracking()
                .ToListAsync(CancellationToken.None);

            if (turnos.FirstOrDefault()?.Estado != EstadoTurno.ABIERTO)
            {
                throw new TurnoCajaNoAbiertoException();
            }

            var ahoraUtc = timeProvider.GetUtcNow();
            var folio = await SiguienteFolioAsync(ahoraUtc);

            var transaccion = new TransaccionPago
            {
                Id = Guid.CreateVersion7(),
                Folio = folio,
                DinningSessionId = request.DinningSessionId,
                TurnoCajaId = turnoCajaId,
                Total = liquidacion.Total,
                Estado = EstadoTransaccion.COMPLETADO,
                FechaCreacionUtc = ahoraUtc.UtcDateTime,
                Detalles = request.DetallesSnapshot.Select(d => new DetalleTransaccion
                {
                    Id = Guid.CreateVersion7(),
                    NombrePlatillo = d.NombrePlatillo,
                    Cantidad = d.Cantidad,
                    PrecioUnitario = d.PrecioUnitario,
                    Importe = d.Importe,
                }).ToList(),
                Desgloses = liquidacion.Pagos.Select(p => new DesglosePago
                {
                    Id = Guid.CreateVersion7(),
                    MetodoPago = p.Metodo,
                    Monto = p.Monto,
                    ReferenciaAutorizacion = p.Referencia,
                }).ToList(),
            };

            db.Transacciones.Add(transaccion);

            // Outbox: el evento se escribe en la misma transacción y solo se entrega tras el COMMIT.
            await publishEndpoint.Publish(
                new PagoCompletado(transaccion.Id, folio, transaccion.DinningSessionId, transaccion.Total, transaccion.FechaCreacionUtc),
                CancellationToken.None);

            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);

            var mensaje = liquidacion.Pagos.Count > 1 ? CobroMensajes.CobroMixtoOk : CobroMensajes.CobroOk;
            return new CobroResponseDTO(folio, transaccion.Total, transaccion.Estado.ToString(), mensaje);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<string> SiguienteFolioAsync(DateTimeOffset ahoraUtc)
    {
        var zona = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZoneId);
        var fecha = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ahoraUtc, zona).DateTime);

        // UPDLOCK + HOLDLOCK serializa a los cobros concurrentes del mismo día hasta el COMMIT (sin huecos en el consecutivo).
        var contadores = await db.FolioContadores
            .FromSqlInterpolated($"SELECT * FROM FolioContador WITH (UPDLOCK, HOLDLOCK) WHERE Fecha = {fecha}")
            .ToListAsync(CancellationToken.None);

        var contador = contadores.FirstOrDefault();
        if (contador is null)
        {
            contador = new FolioContador { Fecha = fecha, Ultimo = 0 };
            db.FolioContadores.Add(contador);
        }

        contador.Ultimo++;
        return FormattableString.Invariant($"TX-{fecha:yyyyMMdd}-{contador.Ultimo:D3}");
    }

    private static bool EsDuplicadoDeSesion(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains(PaymentsDbContext.IndiceSesionUnica, StringComparison.Ordinal);

    private async Task RevertirCargosNoPersistidosAsync(Guid sesionId, List<GatewayChargeRequest> cargos)
    {
        // La pasarela deduplica por Idempotency-Key: si la petición ganadora usó la misma referencia, ese cargo es suyo y no se extorna.
        db.ChangeTracker.Clear();
        var referenciasGanadoras = await db.Transacciones
            .AsNoTracking()
            .Where(t => t.DinningSessionId == sesionId)
            .SelectMany(t => t.Desgloses)
            .Select(d => d.ReferenciaAutorizacion)
            .ToListAsync(CancellationToken.None);

        await RevertirCargosAsync(cargos.Where(c => !referenciasGanadoras.Contains(c.AuthorizationReference)).ToList());
    }

    private async Task RevertirCargosAsync(IReadOnlyCollection<GatewayChargeRequest> cargos)
    {
        foreach (var cargo in cargos)
        {
            try
            {
                await gateway.ReverseAsync(cargo, CancellationToken.None);
                logger.LogWarning("Cargo {Referencia} extornado (sesión {SesionId})", cargo.AuthorizationReference, cargo.DinningSessionId);
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "NO se pudo extornar el cargo {Referencia} de la sesión {SesionId}; requiere conciliación manual",
                    cargo.AuthorizationReference,
                    cargo.DinningSessionId);
            }
        }
    }
}
