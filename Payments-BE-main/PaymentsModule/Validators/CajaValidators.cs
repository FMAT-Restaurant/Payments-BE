using FluentValidation;
using PaymentsModule.Models.Dtos;

namespace PaymentsModule.Validators;

public sealed class AbrirCajaValidator : AbstractValidator<AbrirCajaRequest>
{
    public AbrirCajaValidator()
    {
        RuleFor(x => x.FondoInicial)
            .NotNull().WithMessage("fondo_inicial es obligatorio.")
            .GreaterThanOrEqualTo(0m)
            .PrecisionScale(18, 2, false);
    }
}

public sealed class CerrarCajaValidator : AbstractValidator<CerrarCajaRequest>
{
    public CerrarCajaValidator()
    {
        RuleFor(x => x.EfectivoDeclarado)
            .NotNull().WithMessage("efectivo_declarado es obligatorio.")
            .GreaterThanOrEqualTo(0m)
            .PrecisionScale(18, 2, false);
    }
}
