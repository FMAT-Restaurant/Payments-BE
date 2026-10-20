using FluentValidation;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Models.Entities;
using PaymentsModule.Services;

namespace PaymentsModule.Validators;

public sealed class CobroRequestDTOValidator : AbstractValidator<CobroRequestDTO>
{
    public const string CodigoFondosInsuficientes = "FONDOS_INSUFICIENTES";

    public CobroRequestDTOValidator()
    {
        RuleFor(x => x.DinningSessionId).NotEmpty();

        RuleFor(x => x.DetallesSnapshot).NotEmpty();
        RuleForEach(x => x.DetallesSnapshot).NotNull().SetValidator(new DetalleSnapshotDTOValidator());

        RuleFor(x => x.Pagos).NotEmpty();
        RuleForEach(x => x.Pagos).NotNull().SetValidator(new PagoDTOValidator());

        RuleFor(x => x)
            .Must(x => x.DetallesSnapshot.Sum(d => d.Importe) > 0m)
            .WithName("detalles_snapshot")
            .WithMessage("El total de la orden debe ser mayor a cero.")
            .When(TieneEstructuraValida);

        // AC1: sumatoria de pagos >= total de la orden, antes de entrar al controlador.
        RuleFor(x => x)
            .Must(x => x.Pagos.Sum(p => p.Monto) >= x.DetallesSnapshot.Sum(d => d.Importe))
            .WithName("pagos")
            .WithErrorCode(CodigoFondosInsuficientes)
            .WithMessage(CobroMensajes.FondosInsuficientes)
            .When(TieneEstructuraValida);
    }

    private static bool TieneEstructuraValida(CobroRequestDTO x) =>
        x.DetallesSnapshot is { Count: > 0 } && x.DetallesSnapshot.All(d => d is not null)
        && x.Pagos is { Count: > 0 } && x.Pagos.All(p => p is not null);
}

public sealed class DetalleSnapshotDTOValidator : AbstractValidator<DetalleSnapshotDTO>
{
    public DetalleSnapshotDTOValidator()
    {
        RuleFor(x => x.NombrePlatillo).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Cantidad).GreaterThan(0);
        RuleFor(x => x.PrecioUnitario).GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, false);
        RuleFor(x => x.Importe).GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, false);
        RuleFor(x => x)
            .Must(x => x.Importe == x.Cantidad * x.PrecioUnitario)
            .WithName("importe")
            .WithMessage("El importe debe ser igual a cantidad * precio_unitario.");
    }
}

public sealed class PagoDTOValidator : AbstractValidator<PagoDTO>
{
    public PagoDTOValidator()
    {
        RuleFor(x => x.MetodoPago).IsInEnum();
        RuleFor(x => x.Monto).GreaterThan(0m).PrecisionScale(18, 2, false);
        RuleFor(x => x.ReferenciaAutorizacion).MaximumLength(100);
        RuleFor(x => x.ReferenciaAutorizacion)
            .NotEmpty()
            .When(x => x.MetodoPago == MetodoPago.TARJETA_CREDITO)
            .WithMessage("La referencia de autorización es obligatoria para pagos con tarjeta.");
    }
}
