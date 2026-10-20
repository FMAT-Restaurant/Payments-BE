using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Services;
using PaymentsModule.Validators;

namespace PaymentsModule.Tests;

public class CajaCalculatorTests
{
    [Fact]
    public void CuadreExacto_CierraElTurno()
    {
        var r = CajaCalculator.Cuadrar(500m, 2000m, 2500m);

        Assert.Equal(EstadoTurno.CERRADO, r.Estado);
        Assert.Equal(0m, r.Diferencia);
        Assert.Equal(2500m, r.EfectivoEsperado);
        Assert.Equal("Corte exacto. Turno cerrado.", r.Mensaje);
    }

    [Fact]
    public void Faltante_MarcaDescuadradoConDiferenciaNegativa()
    {
        var r = CajaCalculator.Cuadrar(500m, 2000m, 2400m);

        Assert.Equal(EstadoTurno.DESCUADRADO, r.Estado);
        Assert.Equal(-100m, r.Diferencia);
        Assert.Contains("Faltante", r.Mensaje);
    }

    [Fact]
    public void Sobrante_MarcaDescuadradoConDiferenciaPositiva()
    {
        var r = CajaCalculator.Cuadrar(500m, 2000m, 2550.50m);

        Assert.Equal(EstadoTurno.DESCUADRADO, r.Estado);
        Assert.Equal(50.50m, r.Diferencia);
        Assert.Contains("Sobrante de $50.50", r.Mensaje);
    }

    [Fact]
    public void PrecisionDecimal_NoGeneraFalsosDescuadres()
    {
        var r = CajaCalculator.Cuadrar(0.10m, 0.20m, 0.30m);

        Assert.Equal(EstadoTurno.CERRADO, r.Estado);
    }
}

public class CajaValidatorsTests
{
    [Fact]
    public void Abrir_ConFondoValidoOCero_Pasa()
    {
        var validator = new AbrirCajaValidator();

        Assert.True(validator.Validate(new AbrirCajaRequest { FondoInicial = 500m }).IsValid);
        Assert.True(validator.Validate(new AbrirCajaRequest { FondoInicial = 0m }).IsValid);
    }

    [Fact]
    public void Abrir_SinFondoNegativoOConMasDeDosDecimales_Falla()
    {
        var validator = new AbrirCajaValidator();

        Assert.False(validator.Validate(new AbrirCajaRequest()).IsValid);
        Assert.False(validator.Validate(new AbrirCajaRequest { FondoInicial = -1m }).IsValid);
        Assert.False(validator.Validate(new AbrirCajaRequest { FondoInicial = 10.001m }).IsValid);
    }

    [Fact]
    public void Cerrar_ValidaEfectivoDeclarado()
    {
        var validator = new CerrarCajaValidator();

        Assert.True(validator.Validate(new CerrarCajaRequest { EfectivoDeclarado = 2500m }).IsValid);
        Assert.False(validator.Validate(new CerrarCajaRequest()).IsValid);
        Assert.False(validator.Validate(new CerrarCajaRequest { EfectivoDeclarado = -0.01m }).IsValid);
    }
}
