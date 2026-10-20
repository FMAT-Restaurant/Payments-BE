using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Validators;

namespace PaymentsModule.Tests;

public class HistorialQueryValidatorTests
{
    private readonly HistorialQueryValidator _validator = new();

    [Fact]
    public void ValoresPorDefecto_SonValidos()
    {
        var query = new HistorialQuery();

        Assert.Equal(1, query.PageIndex);
        Assert.Equal(10, query.PageSize);
        Assert.True(_validator.Validate(query).IsValid);
    }

    [Fact]
    public void FiltrosCompletosYPaginaValida_PasanLaValidacion()
    {
        var query = new HistorialQuery
        {
            FechaInicio = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            FechaFin = DateTimeOffset.Parse("2026-09-30T23:59:59Z"),
            Estado = EstadoTransaccion.COMPLETADO,
            MetodoPago = MetodoPago.EFECTIVO,
            PageIndex = 1,
            PageSize = 50,
        };

        Assert.True(_validator.Validate(query).IsValid);
    }

    [Fact]
    public void FechaInicioMayorQueFechaFin_EsInvalida()
    {
        var query = new HistorialQuery
        {
            FechaInicio = DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            FechaFin = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
        };

        var result = _validator.Validate(query);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("rango de fechas"));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public void PaginacionFueraDeRango_EsInvalida(int pageIndex, int pageSize)
    {
        var result = _validator.Validate(new HistorialQuery { PageIndex = pageIndex, PageSize = pageSize });

        Assert.False(result.IsValid);
    }
}
