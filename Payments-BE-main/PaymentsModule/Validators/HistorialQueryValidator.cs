using FluentValidation;
using PaymentsModule.Models.Dtos;

namespace PaymentsModule.Validators;

public sealed class HistorialQueryValidator : AbstractValidator<HistorialQuery>
{
    public const int PageSizeMaximo = 50;

    public HistorialQueryValidator()
    {
        RuleFor(x => x.PageIndex).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PageSizeMaximo);
        RuleFor(x => x.Estado).IsInEnum().When(x => x.Estado.HasValue);
        RuleFor(x => x.MetodoPago).IsInEnum().When(x => x.MetodoPago.HasValue);

        RuleFor(x => x.FechaInicio)
            .Must((query, inicio) => inicio <= query.FechaFin)
            .When(x => x.FechaInicio.HasValue && x.FechaFin.HasValue)
            .WithMessage("El rango de fechas es inválido: fecha_inicio debe ser menor o igual a fecha_fin.");
    }
}
