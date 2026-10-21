using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/pagos")]
public class TransactionDetailController(TransactionDetailService details) : ControllerBase
{
    [HttpGet("{folio}")]
    [ProducesResponseType<TransactionDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetByFolio(string folio, CancellationToken ct)
    {
        // Database failures (e.g. a timeout) are not caught here: the global exception handler answers 500 without internals.
        var detail = await details.GetByFolioAsync(folio, ct);
        if (detail is null)
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Detail = $"The ticket '{folio}' does not exist."
            };
            return new ObjectResult(problem) { StatusCode = problem.Status };
        }

        return Ok(detail);
    }
}
