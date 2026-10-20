using FluentValidation;

namespace PaymentsModule.Models;

public class OpenCashShiftRequestValidator : AbstractValidator<OpenCashShiftRequest>
{
    public OpenCashShiftRequestValidator()
    {
        RuleFor(x => x.InitialFund).GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true);
    }
}

public class CloseCashShiftRequestValidator : AbstractValidator<CloseCashShiftRequest>
{
    public CloseCashShiftRequestValidator()
    {
        RuleFor(x => x.DeclaredCash).GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true);
    }
}

