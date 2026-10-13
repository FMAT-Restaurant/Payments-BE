using FluentValidation;

namespace PaymentsModule.Models;

public class DinningSessionReadyEventValidator : AbstractValidator<DinningSessionReadyEvent>
{
    public DinningSessionReadyEventValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.DinningSessionId).NotEmpty();
        RuleFor(x => x.MesaId).NotEmpty();
        RuleFor(x => x.MeseroId).NotEmpty();
        RuleFor(x => x.Timestamp).NotEmpty();

        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.PlatilloId).NotEmpty();
            item.RuleFor(i => i.Nombre).NotEmpty();
            item.RuleFor(i => i.Cantidad).GreaterThan(0);
            item.RuleFor(i => i.PrecioUnitario).GreaterThanOrEqualTo(0);
        });

        RuleFor(x => x.Totales).NotNull();
        RuleFor(x => x.Totales.TotalEsperado).GreaterThan(0).When(x => x.Totales is not null);
    }
}
