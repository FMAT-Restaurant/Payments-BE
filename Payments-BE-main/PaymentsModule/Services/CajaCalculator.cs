using System.Globalization;
using PaymentsModule.Models.Entities;

namespace PaymentsModule.Services;

public sealed record CuadreCaja(decimal EfectivoEsperado, decimal Diferencia, EstadoTurno Estado, string Mensaje);

/// <summary>Cuadre de caja en decimal: esperado = fondo inicial + cobros en efectivo.</summary>
public static class CajaCalculator
{
    public static CuadreCaja Cuadrar(decimal fondoInicial, decimal ventasEfectivo, decimal efectivoDeclarado)
    {
        var esperado = fondoInicial + ventasEfectivo;
        var diferencia = efectivoDeclarado - esperado;

        if (diferencia == 0m)
        {
            return new CuadreCaja(esperado, 0m, EstadoTurno.CERRADO, "Corte exacto. Turno cerrado.");
        }

        var monto = Math.Abs(diferencia).ToString("F2", CultureInfo.InvariantCulture);
        var mensaje = diferencia > 0m
            ? $"Sobrante de ${monto}. Turno cerrado con descuadre."
            : $"Faltante de ${monto}. Turno cerrado con descuadre.";

        return new CuadreCaja(esperado, diferencia, EstadoTurno.DESCUADRADO, mensaje);
    }
}
