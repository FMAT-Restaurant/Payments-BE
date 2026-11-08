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
    [HttpGet("me")]
    [ProducesResponseType<ActiveCashShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyActiveShift(CancellationToken ct)
    {
        // Cashier identity comes only from the JWT claim (AC2).
        var cashierId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(cashierId))
        {
            return Unauthorized();
        }

        if (!User.IsInRole("Administrador") && !User.IsInRole("Gerente") && !User.IsInRole("Cajero") && !User.IsInRole("Admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErrorResponse("FORBIDDEN_ROLE", "No tiene permisos para realizar esta acción."));
        }

        var shift = await shifts.GetActiveShiftAsync(cashierId, ct);
        if (shift is null)
        {
            return NotFound(new ErrorResponse("CASH_SHIFT_NOT_OPEN", "No hay un turno de caja abierto para este usuario"));
        }

        return Ok(shift);
    }

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

    [HttpGet("{cash_shift_id}/summary")]
    [ProducesResponseType<CashShiftSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSummaryById(
        [FromRoute(Name = "cash_shift_id")] string cashShiftId,
        CancellationToken ct)
    {
        // Only Administrador, Gerente, and Admin roles can supervise cash shifts.
        if (!User.IsInRole("Administrador") && !User.IsInRole("Gerente") && !User.IsInRole("Admin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErrorResponse("FORBIDDEN_ROLE", "No tiene permisos para realizar esta acción."));
        }

        if (!Guid.TryParse(cashShiftId, out var shiftId) || shiftId == Guid.Empty)
        {
            return BadRequest(new ErrorResponse(ErrorResponse.ValidationErrorCode, "El id del turno de caja tiene un formato inválido."));
        }

        var result = await shifts.GetSummaryByIdAsync(shiftId, ct);
        if (result.Outcome == ShiftSummaryByIdOutcome.Found)
        {
            return Ok(result.Response);
        }

        return NotFound(ErrorResponse.CashShiftNotFound());
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

        var result = await shifts.OpenAsync(request, cashierId, ct);
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

