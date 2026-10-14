using FluentValidation;

namespace PaymentsModule.Models;

public class DinningSessionReadyEventValidator : AbstractValidator<DinningSessionReadyEvent>
{
    public DinningSessionReadyEventValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.DinningSessionId).NotEmpty();
        RuleFor(x => x.TableId).NotEmpty();
        RuleFor(x => x.WaiterId).NotEmpty();
        RuleFor(x => x.Timestamp).NotEmpty();

        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.DishId).NotEmpty();
            item.RuleFor(i => i.Name).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0);
            item.RuleFor(i => i.UnitPrice).GreaterThanOrEqualTo(0);
        });

        RuleFor(x => x.Totals).NotNull();
        RuleFor(x => x.Totals.ExpectedTotal).GreaterThan(0).When(x => x.Totals is not null);
    }
}
