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
        });

        RuleFor(x => x.ItemsSnapshot).NotEmpty();
        RuleForEach(x => x.ItemsSnapshot).ChildRules(item =>
        {
            item.RuleFor(i => i.DishName).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0);
            item.RuleFor(i => i.UnitPrice).GreaterThan(0m);
            item.RuleFor(i => i.Amount).GreaterThan(0m);
        });
    }
}
