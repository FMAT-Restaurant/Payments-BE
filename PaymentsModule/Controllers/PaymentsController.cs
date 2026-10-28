using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/pagos")]
public class PaymentsController(ChargeService charges, IValidator<ChargeRequest> validator) : ControllerBase
{
    [HttpPost("cobrar")]
    [ProducesResponseType<ChargeResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Charge(ChargeRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
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

        var result = await charges.ChargeAsync(request, cashierId, ct);
        if (result.Outcome == ChargeOutcome.Completed)
        {
            return StatusCode(StatusCodes.Status201Created, result.Response);
        }

        var problem = new ProblemDetails
        {
            Status = result.Outcome switch
            {
                ChargeOutcome.AlreadyPaid => StatusCodes.Status409Conflict,
                ChargeOutcome.BillNotFound => StatusCodes.Status404NotFound,
                ChargeOutcome.GatewayUnavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status422UnprocessableEntity
            },
            Detail = result.Error
        };
        if (result.Deficit is { } deficit)
        {
            problem.Extensions["deficit"] = deficit;
        }

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }
}
