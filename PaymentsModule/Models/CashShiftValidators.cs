using FluentValidation;

namespace PaymentsModule.Models;

public class OpenCashShiftRequestValidator : AbstractValidator<OpenCashShiftRequest>
{
    public OpenCashShiftRequestValidator()
    {
        RuleFor(x => x.InitialFund).NotNull().GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true);
    }
}

public class CloseCashShiftRequestValidator : AbstractValidator<CloseCashShiftRequest>
{
    public CloseCashShiftRequestValidator()
    {
        RuleFor(x => x.DeclaredCash).NotNull().GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public class ForceCloseCashShiftRequestValidator : AbstractValidator<ForceCloseCashShiftRequest>
{
    public ForceCloseCashShiftRequestValidator()
    {
        RuleFor(x => x.DeclaredCash).NotNull().GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true);
        RuleFor(x => x.ForceCloseReason).NotEmpty().MinimumLength(10);
    }
}

