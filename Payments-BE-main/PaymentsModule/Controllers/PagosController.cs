using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Filters;
using PaymentsModule.Models.Dtos;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/pagos")]
public class PagosController(ITransaccionQueryService queryService) : ControllerBase
{
    /// <summary>Historial de ventas paginado, con filtros opcionales por fechas, estado y método de pago.</summary>
    [HttpGet]
    [ServiceFilter(typeof(ValidationFilter<HistorialQuery>))]
    [ProducesResponseType<PagedResultDTO<HistorialItemDTO>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetHistorial([FromQuery] HistorialQuery query, CancellationToken ct)
    {
        // Un fallo de SQL (p. ej. timeout) lo captura el manejador global y responde 500 sin exponer detalles.
        var resultado = await queryService.ObtenerHistorialAsync(query, ct);
        return Ok(resultado);
    }

    /// <summary>Recupera el detalle (ticket) de una transacción a partir de su folio.</summary>
    [HttpGet("{folio}")]
    [ProducesResponseType<TransaccionDetalleDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPorFolio(string folio, CancellationToken ct)
    {
        // Un fallo de EF Core (p. ej. timeout) lo captura el manejador global de excepciones y responde 500.
        var detalle = await queryService.ObtenerPorFolioAsync(folio, ct);

        return detalle is null
            ? Problem(detail: $"El ticket con folio '{folio}' no existe.", statusCode: StatusCodes.Status404NotFound)
            : Ok(detalle);
    }
}
