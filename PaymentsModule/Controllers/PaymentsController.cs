using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize(Roles = "Administrador,Gerente,Cajero")]
[Route("api/v1/payments")]
public class PaymentsController(ChargeService charges, IValidator<ChargeRequest> validator) : ControllerBase
{
    [HttpPost("charge")]
    [ProducesResponseType<ChargeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Charge(ChargeRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
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

        var result = await charges.ChargeAsync(request, cashierId, ct);
        if (result.Outcome == ChargeOutcome.Completed)
        {
            return StatusCode(StatusCodes.Status201Created, result.Response);
        }

        // Standard error contract agreed on 05/11/2026: { "code": "STRING_ENUM", "message": "Texto en español" }.
        var (code, status) = result.Outcome switch
        {
            ChargeOutcome.AlreadyPaid => ("TRANSACTION_ALREADY_PAID", StatusCodes.Status409Conflict),
            ChargeOutcome.BillNotFound => ("BILL_NOT_FOUND", StatusCodes.Status404NotFound),
            ChargeOutcome.NoOpenShift => ("CASH_SHIFT_NOT_OPEN", StatusCodes.Status403Forbidden),
            ChargeOutcome.AuthorizationReferenceRequired => ("AUTHORIZATION_REFERENCE_REQUIRED", StatusCodes.Status422UnprocessableEntity),
            ChargeOutcome.InsufficientFunds => ("INSUFFICIENT_FUNDS", StatusCodes.Status422UnprocessableEntity),
            ChargeOutcome.ElectronicOverpayment => ("CARD_AMOUNT_EXCEEDS_BALANCE", StatusCodes.Status422UnprocessableEntity),
            ChargeOutcome.CardCoversFullAmount => ("CARD_COVERS_FULL_AMOUNT", StatusCodes.Status422UnprocessableEntity),
            _ => ("UNKNOWN_ERROR", StatusCodes.Status500InternalServerError)
        };

        return new ObjectResult(new ErrorResponse(code, result.Error!, result.Deficit)) { StatusCode = status };
    }
}
