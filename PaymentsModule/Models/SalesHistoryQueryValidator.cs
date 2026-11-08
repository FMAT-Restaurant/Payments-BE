using FluentValidation;

namespace PaymentsModule.Models;

public class SalesHistoryQueryValidator : AbstractValidator<SalesHistoryQuery>
{
    public const int MaxPageSize = 50;

    public SalesHistoryQueryValidator()
    {
        RuleFor(x => x.PageIndex).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);

        RuleFor(x => x.Status)
            .Must(value => WireEnum.TryParse<TransactionStatus>(value, out _))
            .When(x => x.Status is not null)
            .WithMessage("estado debe ser COMPLETADO o CANCELADO.");

        RuleFor(x => x.Method)
            .Must(value => WireEnum.TryParse<PaymentMethod>(value, out _))
            .When(x => x.Method is not null)
            .WithMessage("metodo_pago debe ser EFECTIVO, TARJETA_CREDITO o TARJETA_DEBITO.");

        RuleFor(x => x.From)
            .Must((query, start) => start <= query.To)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage("El rango de fechas no es válido: fecha_inicio no puede ser posterior a fecha_fin.");
    }
}
