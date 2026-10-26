using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize(Roles = "Gerente,Administrador,Admin")]
[Route("api/v1/pagos")]
public class SaleCancellationController(SaleCancellationService cancellations, IValidator<CancelSaleRequest> validator) : ControllerBase
{
    [HttpPost("{folio}/cancelar")]
    [ProducesResponseType<CancelSaleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Cancel(string folio, CancelSaleRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        // Authorizer identity comes only from the JWT forwarded by the API Gateway (RNF-04).
        var authorizerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(authorizerId))
        {
            return Unauthorized();
        }

        var result = await cancellations.CancelAsync(folio, request.Reason!.Trim(), authorizerId, ct);
        if (result.Outcome == CancelSaleOutcome.Cancelled)
        {
            return Ok(result.Response);
        }

        var problem = new ProblemDetails
        {
            Status = result.Outcome switch
            {
                CancelSaleOutcome.NotFound => StatusCodes.Status404NotFound,
                CancelSaleOutcome.AlreadyCancelled => StatusCodes.Status409Conflict,
                CancelSaleOutcome.MissingAuthorizationReference => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status502BadGateway
            },
            Detail = result.Error
        };

        return new ObjectResult(problem) { StatusCode = problem.Status };
    }
}
