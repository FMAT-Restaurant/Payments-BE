using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/cash-shifts")]
public class CashShiftsController(
    CashShiftService shifts,
    IValidator<OpenCashShiftRequest> openValidator,
    IValidator<CloseCashShiftRequest> closeValidator) : ControllerBase
{
    [HttpPost("open")]
    [ProducesResponseType<OpenCashShiftResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Open(OpenCashShiftRequest request, CancellationToken ct)
    {
        var validation = await openValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        // Cashier identity comes only from the JWT forwarded by the API Gateway (RNF-04).
        var cashierId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(cashierId))
        {
            return Unauthorized();
        }

        var result = await shifts.OpenAsync(request, cashierId, ct);
        if (result.Outcome == OpenShiftOutcome.Opened)
        {
            return StatusCode(StatusCodes.Status201Created, result.Response);
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Detail = result.Error
        };

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }

    [HttpPost("close")]
    [ProducesResponseType<CloseCashShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Close(CloseCashShiftRequest request, CancellationToken ct)
    {
        var validation = await closeValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        // Cashier identity comes only from the JWT forwarded by the API Gateway (RNF-04).
        var cashierId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(cashierId))
        {
            return Unauthorized();
        }

        var result = await shifts.CloseAsync(request, cashierId, ct);
        if (result.Outcome == CloseShiftOutcome.Closed)
        {
            return Ok(result.Response);
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = result.Error
        };

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }
}

