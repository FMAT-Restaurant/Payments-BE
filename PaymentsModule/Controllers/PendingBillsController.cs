using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize(Roles = "Administrador,Gerente,Admin,Cajero")]
[Route("api/v1/payments/pendings")]
public class PendingBillsController(
    PendingBillService bills,
    ILogger<PendingBillsController> logger) : ControllerBase
{
    /// <summary>
    /// Returns all active pending bills in the cashier inbox without querying SQL (HU-FE-02 / PMC-5).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PendingBillResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        try
        {
            var pendings = await bills.GetAllPendingAsync(ct);
            return Ok(pendings);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogError(ex, "Failed to connect to Redis cache while fetching pending bills.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ErrorResponse.RedisTimeout());
        }
    }

    /// <summary>
    /// Returns a single pending bill by its dining session id to rehydrate the payment screen on reload (F5).
    /// </summary>
    [HttpGet("{dinning_session_id}")]
    [ProducesResponseType<PendingBillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetById(
        [FromRoute(Name = "dinning_session_id")] string dinningSessionId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(dinningSessionId, out var sessionId) || sessionId == Guid.Empty)
        {
            return BadRequest(new ErrorResponse(ErrorResponse.ValidationErrorCode, "El id de sesión tiene un formato inválido."));
        }

        try
        {
            var pending = await bills.GetPendingBillAsync(sessionId, ct);
            if (pending is null)
            {
                return NotFound(ErrorResponse.SessionNotFound());
            }

            return Ok(pending);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogError(ex, "Failed to connect to Redis cache while fetching pending bill {SessionId}.", sessionId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ErrorResponse.RedisTimeout());
        }
    }

    private static bool IsRedisFailure(Exception ex) =>
        ex is TimeoutException
        || ex.GetType().Name.Contains("Redis", StringComparison.OrdinalIgnoreCase)
        || (ex.Message != null && ex.Message.Contains("Redis", StringComparison.OrdinalIgnoreCase))
        || (ex.InnerException != null && IsRedisFailure(ex.InnerException));
}
