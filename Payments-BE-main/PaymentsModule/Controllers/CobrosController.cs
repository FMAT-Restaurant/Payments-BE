using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Filters;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/cobros")]
public class CobrosController(ICobroService cobroService) : ControllerBase
{
    /// <summary>Procesa un cobro (simple o mixto) de una cuenta.</summary>
    [HttpPost]
    [ServiceFilter(typeof(CobroRequestValidationFilter))]
    [ProducesResponseType<CobroResponseDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Post([FromBody] CobroRequestDTO request, CancellationToken ct)
    {
        // AC3: la identidad del cajero viene de los claims del JWT, nunca del payload.
        var cajeroId = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(cajeroId))
        {
            return Unauthorized();
        }

        var result = await cobroService.ProcesarCobroAsync(request, cajeroId, ct);

        if (result.EsExitoso)
        {
            return StatusCode(StatusCodes.Status201Created, result.Respuesta);
        }

        var status = result.Error switch
        {
            CobroError.YaPagado => StatusCodes.Status409Conflict,
            CobroError.FondosInsuficientes
                or CobroError.ExcedenteNoAbsorbible
                or CobroError.TurnoCajaInactivo
                or CobroError.TarjetaRechazada => StatusCodes.Status422UnprocessableEntity,
            CobroError.PasarelaNoDisponible => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Problem(detail: result.Mensaje, statusCode: status);
    }
}
