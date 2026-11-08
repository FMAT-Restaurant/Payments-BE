using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/payments/cash-shifts")]
public class CashShiftsController(
    CashShiftService shifts,
    IValidator<OpenCashShiftRequest> openValidator,
    IValidator<CloseCashShiftRequest> closeValidator) : ControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType<CashShiftSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        // Cashier identity comes only from the JWT forwarded by the API Gateway (RNF-04).
        var cashierId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(cashierId))
        {
            return Unauthorized();
        }

        var result = await shifts.GetSummaryAsync(cashierId, ct);
        if (result.Outcome == ShiftSummaryOutcome.Found)
        {
            return Ok(result.Response);
        }

        return NotFound(new ErrorResponse("CASH_SHIFT_NOT_OPEN", result.Error!));
    }

    [HttpPost("open")]
    [ProducesResponseType<OpenCashShiftResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Open(OpenCashShiftRequest request, CancellationToken ct)
    {
        var validation = await openValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return BadRequest(ErrorResponse.Validation(validation));
        }

        // Cashier identity comes only from the JWT forwarded by the API Gateway (RNF-04).
        var cashierId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(cashierId))
        {
            return Unauthorized();
        }

        // PMC-8 AC3: display name from the JWT (OIDC "name", or ClaimTypes.Name when the handler maps "unique_name").
        // Pending with Auth which claim carries it; null until then.
        var cashierName = User.FindFirstValue("name") ?? User.FindFirstValue(ClaimTypes.Name);

        var result = await shifts.OpenAsync(request, cashierId, cashierName, ct);
        if (result.Outcome == OpenShiftOutcome.Opened)
        {
            return StatusCode(StatusCodes.Status201Created, result.Response);
        }

        return Conflict(new ErrorResponse("CASH_SHIFT_ALREADY_OPEN", result.Error!));
    }

    [HttpPost("close")]
    [ProducesResponseType<CloseCashShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Close(CloseCashShiftRequest request, CancellationToken ct)
    {
        var validation = await closeValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return BadRequest(ErrorResponse.Validation(validation));
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

        return UnprocessableEntity(new ErrorResponse("CASH_SHIFT_NOT_OPEN", result.Error!));
    }
}

