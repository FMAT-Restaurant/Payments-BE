using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Filters;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/caja")]
public class CajaController(ICajaService cajaService, ICajeroContext cajero) : ControllerBase
{
    /// <summary>Abre el turno de caja del cajero autenticado con un fondo inicial.</summary>
    [HttpPost("abrir")]
    [ServiceFilter(typeof(ValidationFilter<AbrirCajaRequest>))]
    [ProducesResponseType<AbrirCajaResponseDTO>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Abrir([FromBody] AbrirCajaRequest request, CancellationToken ct)
    {
        // AC1: el cajero sale del JWT, nunca del payload.
        if (cajero.CajeroId is not { } cajeroId)
        {
            return Unauthorized();
        }

        var result = await cajaService.AbrirAsync(request.FondoInicial!.Value, cajeroId, ct);

        return result.EsExitoso
            ? StatusCode(StatusCodes.Status201Created, result.Valor)
            : Problem(detail: result.Mensaje, statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Cierra el turno abierto del cajero y lo cuadra contra el efectivo declarado.</summary>
    [HttpPost("cerrar")]
    [ServiceFilter(typeof(ValidationFilter<CerrarCajaRequest>))]
    [ProducesResponseType<CerrarCajaResponseDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Cerrar([FromBody] CerrarCajaRequest request, CancellationToken ct)
    {
        if (cajero.CajeroId is not { } cajeroId)
        {
            return Unauthorized();
        }

        var result = await cajaService.CerrarAsync(request.EfectivoDeclarado!.Value, cajeroId, ct);

        return result.EsExitoso
            ? Ok(result.Valor)
            : Problem(detail: result.Mensaje, statusCode: StatusCodes.Status409Conflict);
    }
}
