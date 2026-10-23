using FluentValidation;

namespace PaymentsModule.Models;

public class CancelSaleRequestValidator : AbstractValidator<CancelSaleRequest>
{
    public const int MaxReasonLength = 500;

    public CancelSaleRequestValidator()
    {
        // Required (null, empty and whitespace-only fail). The reason is trimmed before it is stored, so the limit applies to the trimmed text.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .Must(reason => reason is null || reason.Trim().Length <= MaxReasonLength)
            .WithMessage($"'motivo' must not exceed {MaxReasonLength} characters.");
    }
}
