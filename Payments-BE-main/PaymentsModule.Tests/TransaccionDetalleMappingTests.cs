using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;

namespace PaymentsModule.Tests;

public class TransaccionDetalleMappingTests
{
    [Fact]
    public void ToDetalleDto_MapeaCabeceraDetallesYDesglosesSinPerderPrecision()
    {
        var turno = Guid.NewGuid();
        var sesion = Guid.NewGuid();
        var entidad = new TransaccionPago
        {
            Folio = "TX-10042",
            TurnoCajaId = turno,
            DinningSessionId = sesion,
            Total = 750.00m,
            Estado = EstadoTransaccion.COMPLETADO,
            Detalles =
            [
                new DetalleTransaccion { NombrePlatillo = "Corte Ribeye", Cantidad = 1, PrecioUnitario = 500.00m, Importe = 500.00m },
                new DetalleTransaccion { NombrePlatillo = "Vino Tinto", Cantidad = 1, PrecioUnitario = 250.00m, Importe = 250.00m },
            ],
            Desgloses =
            [
                new DesglosePago { MetodoPago = MetodoPago.TARJETA_CREDITO, Monto = 750.00m, ReferenciaAutorizacion = "AUTH-123456" },
            ],
        };

        var dto = entidad.ToDetalleDto();

        Assert.Equal("TX-10042", dto.Folio);
        Assert.Equal(turno, dto.TurnoCajaId);
        Assert.Equal(sesion, dto.DinningSessionId);
        Assert.Equal(750.00m, dto.Total);
        Assert.Equal("COMPLETADO", dto.Estado);
        Assert.Equal(2, dto.Detalles.Count);
        Assert.Equal("Vino Tinto", dto.Detalles[1].NombrePlatillo);
        Assert.Equal(250.00m, dto.Detalles[1].PrecioUnitario);
        var desglose = Assert.Single(dto.Desgloses);
        Assert.Equal("TARJETA_CREDITO", desglose.MetodoPago);
        Assert.Equal("AUTH-123456", desglose.ReferenciaAutorizacion);
    }
}
