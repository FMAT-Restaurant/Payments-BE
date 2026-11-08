using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/payments")]
public class SalesHistoryController(SalesHistoryService history, IValidator<SalesHistoryQuery> validator) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResponse<SalesHistoryItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get([FromQuery] SalesHistoryQuery query, CancellationToken ct)
    {
        // Invalid input is rejected before touching the database.
        var validation = await validator.ValidateAsync(query, ct);
        if (!validation.IsValid)
        {
            return BadRequest(ErrorResponse.Validation(validation));
        }

        // Database failures (e.g. a timeout) are not caught here: the global exception handler answers 500 without internals.
        return Ok(await history.GetAsync(query.ToFilter(), ct));
    }
}
