using FluentValidation;

namespace PaymentsModule.Models;

public class SalesHistoryQueryValidator : AbstractValidator<SalesHistoryQuery>
{
    public const int MaxPageSize = 50;

    public SalesHistoryQueryValidator()
    {
        RuleFor(x => x.PageIndex).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);

        RuleFor(x => x.StartDate)
            .Must((query, start) => start < query.EndDate)
            .When(x => x.StartDate.HasValue && x.EndDate.HasValue)
            .WithMessage("La fecha de inicio debe ser anterior a la fecha de fin.");
    }
}
