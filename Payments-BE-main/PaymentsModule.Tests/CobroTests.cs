using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Services;
using PaymentsModule.Validators;

namespace PaymentsModule.Tests;

public class CobroCalculatorTests
{
    private static PagoDTO Pago(MetodoPago metodo, decimal monto, string? referencia = null) =>
        new() { MetodoPago = metodo, Monto = monto, ReferenciaAutorizacion = referencia };

    [Fact]
    public void CobroMixtoExacto_EsCompletoSinCambio()
    {
        var r = CobroCalculator.Liquidar(500m, [Pago(MetodoPago.EFECTIVO, 200m), Pago(MetodoPago.TARJETA_CREDITO, 300m, "AUTH-1")]);

        Assert.Equal(EstadoLiquidacion.Completa, r.Estado);
        Assert.Equal(0m, r.Cambio);
        Assert.Equal(2, r.Pagos.Count);
    }

    [Fact]
    public void MontosInsuficientes_SeRechazan()
    {
        var r = CobroCalculator.Liquidar(500m, [Pago(MetodoPago.EFECTIVO, 450m)]);

        Assert.Equal(EstadoLiquidacion.FondosInsuficientes, r.Estado);
    }

    [Fact]
    public void ExcedenteEnEfectivo_SeConvierteEnCambioYNoSePersiste()
    {
        var r = CobroCalculator.Liquidar(500m, [Pago(MetodoPago.EFECTIVO, 300m), Pago(MetodoPago.TARJETA_CREDITO, 300m, "AUTH-1")]);

        Assert.Equal(EstadoLiquidacion.Completa, r.Estado);
        Assert.Equal(100m, r.Cambio);
        Assert.Equal(500m, r.Pagos.Sum(p => p.Monto));
        Assert.Equal(200m, r.Pagos.Single(p => p.Metodo == MetodoPago.EFECTIVO).Monto);
    }

    [Fact]
    public void ExcedenteSinEfectivo_SeRechaza()
    {
        var r = CobroCalculator.Liquidar(500m, [Pago(MetodoPago.TARJETA_CREDITO, 600m, "AUTH-1")]);

        Assert.Equal(EstadoLiquidacion.ExcedenteNoAbsorbible, r.Estado);
    }

    [Fact]
    public void ExcedenteMayorAlEfectivo_SeRechaza()
    {
        var r = CobroCalculator.Liquidar(500m, [Pago(MetodoPago.EFECTIVO, 50m), Pago(MetodoPago.TARJETA_CREDITO, 600m, "AUTH-1")]);

        Assert.Equal(EstadoLiquidacion.ExcedenteNoAbsorbible, r.Estado);
    }

    [Fact]
    public void AritmeticaDecimal_NoPierdePrecision()
    {
        var r = CobroCalculator.Liquidar(0.30m, [Pago(MetodoPago.EFECTIVO, 0.10m), Pago(MetodoPago.EFECTIVO, 0.20m)]);

        Assert.Equal(EstadoLiquidacion.Completa, r.Estado);
        Assert.Equal(0m, r.Cambio);
    }
}

public class CobroRequestDTOValidatorTests
{
    private readonly CobroRequestDTOValidator _validator = new();

    private static CobroRequestDTO Solicitud(params PagoDTO[] pagos) => new()
    {
        DinningSessionId = Guid.NewGuid(),
        DetallesSnapshot =
        [
            new DetalleSnapshotDTO { NombrePlatillo = "Corte Ribeye", Cantidad = 1, PrecioUnitario = 500.00m, Importe = 500.00m },
        ],
        Pagos = [.. pagos],
    };

    [Fact]
    public void CobroMixtoValido_PasaLaValidacion()
    {
        var result = _validator.Validate(Solicitud(
            new PagoDTO { MetodoPago = MetodoPago.EFECTIVO, Monto = 200m },
            new PagoDTO { MetodoPago = MetodoPago.TARJETA_CREDITO, Monto = 300m, ReferenciaAutorizacion = "AUTH-987654" }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void SumaMenorAlTotal_ProduceErrorDeFondosInsuficientes()
    {
        var result = _validator.Validate(Solicitud(new PagoDTO { MetodoPago = MetodoPago.EFECTIVO, Monto = 450m }));

        var error = Assert.Single(result.Errors);
        Assert.Equal(CobroRequestDTOValidator.CodigoFondosInsuficientes, error.ErrorCode);
        Assert.Equal(CobroMensajes.FondosInsuficientes, error.ErrorMessage);
    }

    [Fact]
    public void TarjetaSinReferencia_EsInvalida()
    {
        var result = _validator.Validate(Solicitud(new PagoDTO { MetodoPago = MetodoPago.TARJETA_CREDITO, Monto = 500m }));

        Assert.False(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == CobroRequestDTOValidator.CodigoFondosInsuficientes);
    }

    [Fact]
    public void MontoNegativoOCeroYMasDeDosDecimales_SonInvalidos()
    {
        Assert.False(_validator.Validate(Solicitud(new PagoDTO { MetodoPago = MetodoPago.EFECTIVO, Monto = 0m })).IsValid);
        Assert.False(_validator.Validate(Solicitud(new PagoDTO { MetodoPago = MetodoPago.EFECTIVO, Monto = 500.001m })).IsValid);
    }

    [Fact]
    public void ImporteInconsistenteConCantidadPorPrecio_EsInvalido()
    {
        var request = new CobroRequestDTO
        {
            DinningSessionId = Guid.NewGuid(),
            DetallesSnapshot = [new DetalleSnapshotDTO { NombrePlatillo = "Tacos", Cantidad = 2, PrecioUnitario = 50m, Importe = 90m }],
            Pagos = [new PagoDTO { MetodoPago = MetodoPago.EFECTIVO, Monto = 100m }],
        };

        Assert.False(_validator.Validate(request).IsValid);
    }
}
