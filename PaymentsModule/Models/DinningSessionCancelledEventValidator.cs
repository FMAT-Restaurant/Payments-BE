using FluentValidation;

namespace PaymentsModule.Models;

public class DinningSessionCancelledEventValidator : AbstractValidator<DinningSessionCancelledEvent>
{
    public DinningSessionCancelledEventValidator()
    {
        RuleFor(x => x.DinningSessionId).NotEmpty();
    }
}
