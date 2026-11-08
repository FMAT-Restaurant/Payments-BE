using FluentValidation;

namespace PaymentsModule.Models;

public class ChargeRequestValidator : AbstractValidator<ChargeRequest>
{
    public ChargeRequestValidator()
    {
        RuleFor(x => x.DinningSessionId).NotEmpty();
        // Tip may be zero; every other amount must be positive. Scale 2 matches the decimal(18,2) columns.
        RuleFor(x => x.Tip).GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true);

        RuleFor(x => x.Payments).NotEmpty();
        RuleForEach(x => x.Payments).ChildRules(p =>
        {
            p.RuleFor(i => i.Method).IsInEnum();
            p.RuleFor(i => i.Amount).GreaterThan(0m).PrecisionScale(18, 2, true);
            // A missing reference is a business rule (422 AUTHORIZATION_REFERENCE_REQUIRED, PMC-7), not a 400.
            p.RuleFor(i => i.AuthorizationReference).MaximumLength(100);
        });
    }
}
