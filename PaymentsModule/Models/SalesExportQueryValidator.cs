using FluentValidation;

namespace PaymentsModule.Models;

public class SalesExportQueryValidator : AbstractValidator<SalesExportQuery>
{
    /// <summary>Upper bound of the export window, to protect the database and the network (PMC-15 AC2).</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    public SalesExportQueryValidator()
    {
        RuleFor(x => x.StartDate)
            .NotNull()
            .OverridePropertyName("start_date")
            .WithMessage("El parámetro start_date es obligatorio.");

        RuleFor(x => x.EndDate)
            .NotNull()
            .OverridePropertyName("end_date")
            .WithMessage("El parámetro end_date es obligatorio.");

        When(x => x.StartDate.HasValue && x.EndDate.HasValue, () =>
        {
            RuleFor(x => x.EndDate)
                .Must((query, end) => end > query.StartDate)
                .WithMessage("El rango de fechas no es válido: inicio no puede ser posterior a fin.")
                .Must((query, end) => end - query.StartDate <= MaxRange)
                .WithMessage("El rango de fechas no puede exceder los 31 días.")
                .OverridePropertyName("end_date");
        });
    }
}
