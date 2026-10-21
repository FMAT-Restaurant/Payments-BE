using FluentValidation;

namespace PaymentsModule.Models;

public class SalesExportQueryValidator : AbstractValidator<SalesExportQuery>
{
    /// <summary>Upper bound of the export window, to protect the database and the network (PMC-15 AC1).</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    public SalesExportQueryValidator()
    {
        RuleFor(x => x.Start).NotNull().OverridePropertyName("inicio")
            .WithMessage("El parámetro 'inicio' es obligatorio.");
        RuleFor(x => x.End).NotNull().OverridePropertyName("fin")
            .WithMessage("El parámetro 'fin' es obligatorio.");

        When(x => x.Start.HasValue && x.End.HasValue, () =>
        {
            RuleFor(x => x.End)
                .Must((query, end) => end >= query.Start)
                .WithMessage("La fecha 'fin' no puede ser anterior a 'inicio'.")
                .Must((query, end) => end - query.Start <= MaxRange)
                .WithMessage("El rango de fechas no puede exceder los 31 días")
                .OverridePropertyName("fin");
        });
    }
}
