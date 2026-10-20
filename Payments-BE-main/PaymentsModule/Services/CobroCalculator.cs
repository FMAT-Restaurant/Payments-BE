using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;

namespace PaymentsModule.Services;

public enum EstadoLiquidacion
{
    Completa,
    FondosInsuficientes,
    ExcedenteNoAbsorbible,
}

public sealed record PagoAplicado(MetodoPago Metodo, decimal Monto, string? Referencia);

public sealed record Liquidacion(
    EstadoLiquidacion Estado,
    decimal Total,
    decimal TotalPagado,
    decimal Cambio,
    IReadOnlyList<PagoAplicado> Pagos);

/// <summary>Motor de cálculo: toda la aritmética es decimal (nunca float/double).</summary>
public static class CobroCalculator
{
    public static Liquidacion Liquidar(decimal total, IReadOnlyList<PagoDTO> pagos)
    {
        var pagado = pagos.Sum(p => p.Monto);

        if (pagado < total)
        {
            return new Liquidacion(EstadoLiquidacion.FondosInsuficientes, total, pagado, 0m, []);
        }

        var excedente = pagado - total;
        var efectivo = pagos.Where(p => p.MetodoPago == MetodoPago.EFECTIVO).Sum(p => p.Monto);

        if (excedente > efectivo)
        {
            return new Liquidacion(EstadoLiquidacion.ExcedenteNoAbsorbible, total, pagado, 0m, []);
        }

        var aplicados = pagos
            .Select(p => new PagoAplicado(p.MetodoPago, p.Monto, p.ReferenciaAutorizacion))
            .ToList();

        // El cambio se descuenta del efectivo (de atrás hacia adelante) y no se persiste como ingreso.
        var restante = excedente;
        for (var i = aplicados.Count - 1; i >= 0 && restante > 0m; i--)
        {
            if (aplicados[i].Metodo != MetodoPago.EFECTIVO)
            {
                continue;
            }

            var descuento = Math.Min(restante, aplicados[i].Monto);
            aplicados[i] = aplicados[i] with { Monto = aplicados[i].Monto - descuento };
            restante -= descuento;
        }

        return new Liquidacion(
            EstadoLiquidacion.Completa,
            total,
            pagado,
            excedente,
            aplicados.Where(p => p.Monto > 0m).ToList());
    }
}
