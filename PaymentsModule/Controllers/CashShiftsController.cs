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
    IValidator<CloseCashShiftRequest> closeValidator,
    IValidator<ForceCloseCashShiftRequest> forceCloseValidator) : ControllerBase
{
    [HttpGet("me")]
    [Authorize(Roles = "Administrador,Gerente,Cajero")]
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

        var result = await shifts.GetActiveAsync(cashierId, ct);
        if (result.Outcome == ActiveCashShiftOutcome.Found)
        {
            return Ok(result.Response);
        }

        return NotFound(new ErrorResponse("CASH_SHIFT_NOT_OPEN", result.Error!));
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
    [Authorize(Roles = "Administrador,Gerente")]
    [ProducesResponseType<CashShiftSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSummaryById(
        [FromRoute(Name = "cash_shift_id")] string cashShiftId,
        CancellationToken ct)
    {
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

    [HttpGet("open")]
    [Authorize(Roles = "Administrador,Gerente")]
    [ProducesResponseType<IReadOnlyList<OpenShiftListItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOpenShifts(CancellationToken ct)
    {
        var openShifts = await shifts.GetOpenShiftsAsync(ct);
        return Ok(openShifts);
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

    [HttpPost("{cash_shift_id}/force-close")]
    [Authorize(Roles = "Administrador,Gerente")]
    [ProducesResponseType<ForceCloseCashShiftResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ForceClose(
        [FromRoute(Name = "cash_shift_id")] string cashShiftId,
        ForceCloseCashShiftRequest request,
        CancellationToken ct)
    {
        if (!Guid.TryParse(cashShiftId, out var shiftId) || shiftId == Guid.Empty)
        {
            return BadRequest(new ErrorResponse(ErrorResponse.ValidationErrorCode, "El id del turno de caja tiene un formato inválido."));
        }

        var validation = await forceCloseValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return BadRequest(ErrorResponse.Validation(validation));
        }

        // Supervisor identity comes exclusively from the JWT claim (AC2).
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(adminId))
        {
            return Unauthorized();
        }

        var result = await shifts.ForceCloseAsync(shiftId, request, adminId, ct);
        return result.Outcome switch
        {
            ForceCloseShiftOutcome.Closed => Ok(result.Response),
            ForceCloseShiftOutcome.ShiftNotFound => NotFound(ErrorResponse.CashShiftNotFound()),
            ForceCloseShiftOutcome.ShiftAlreadyClosed => Conflict(ErrorResponse.CashShiftClosed()),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse("INTERNAL_ERROR", "Ocurrió un error interno."))
        };
    }
}

