using FluentValidation;

namespace PaymentsModule.Models;

public class SalesExportQueryValidator : AbstractValidator<SalesExportQuery>
{
    /// <summary>Upper bound of the export window, to protect the database and the network (PMC-15 AC1).</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    public SalesExportQueryValidator()
    {
        RuleFor(x => x.Start).NotNull().OverridePropertyName("inicio")
            .WithMessage("inicio is required.");
        RuleFor(x => x.End).NotNull().OverridePropertyName("fin")
            .WithMessage("fin is required.");

        When(x => x.Start.HasValue && x.End.HasValue, () =>
        {
            RuleFor(x => x.End)
                .Must((query, end) => end >= query.Start)
                .WithMessage("The date range is invalid: inicio must not be after fin.")
                .Must((query, end) => end - query.Start <= MaxRange)
                .WithMessage("The date range cannot exceed 31 days.")
                .OverridePropertyName("fin");
        });
    }
}
