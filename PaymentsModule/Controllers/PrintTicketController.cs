using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/pagos")]
public class PrintTicketController(PrintTicketService tickets) : ControllerBase
{
    /// <summary>Returns the print DTO of a sale for the original print or any reprint.</summary>
    [HttpGet("{folio}/ticket-impresion")]
    [ProducesResponseType<PrintTicketResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPrintTicket(string folio, CancellationToken ct)
    {
        // Database failures are not caught here: the global exception handler answers 500 without internals.
        var ticket = await tickets.GetAsync(folio, ct);
        if (ticket is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Detail = $"The ticket '{folio}' does not exist."
            });
        }

        return Ok(ticket);
    }
}
