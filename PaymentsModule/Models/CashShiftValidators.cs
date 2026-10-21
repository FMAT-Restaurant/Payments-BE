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
    }
}

