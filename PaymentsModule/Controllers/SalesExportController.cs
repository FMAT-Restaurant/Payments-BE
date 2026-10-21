using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Controllers;

[ApiController]
[Authorize(Roles = "Contador,Administrador")]
[Route("api/v1/pagos")]
public class SalesExportController(
    SalesExportService exports,
    IValidator<SalesExportQuery> validator,
    ILogger<SalesExportController> logger) : ControllerBase
{
    /// <summary>Streams the sales history of a window of at most 31 days as a CSV attachment.</summary>
    [HttpGet("exportar")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(
        [FromQuery(Name = "inicio")] DateTimeOffset? start,
        [FromQuery(Name = "fin")] DateTimeOffset? end,
        CancellationToken ct)
    {
        // Validated before any database access (PMC-15 scenario 2).
        var validation = await validator.ValidateAsync(new SalesExportQuery(start, end), ct);
        if (!validation.IsValid)
        {
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        }

        // No Content-Length: Kestrel sends the body with Transfer-Encoding: chunked.
        Response.ContentType = SalesExportService.ContentType;
        Response.Headers.ContentDisposition = $"attachment; filename=\"{SalesExportService.FileName(start!.Value, end!.Value)}\"";

        try
        {
            var rows = await SalesExportService.WriteCsvAsync(exports.StreamRows(start.Value, end.Value), Response.Body, ct);
            logger.LogInformation("Sales export {Start:o} - {End:o} streamed {Rows} rows.", start, end, rows);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Sales export {Start:o} - {End:o} cancelled by the client.", start, end);
        }
        // Once bytes are on the wire the status cannot become 500: close the connection so the client sees
        // an incomplete download instead of a file that looks finished (PMC-15 scenario 3).
        catch (Exception ex) when (Response.HasStarted)
        {
            logger.LogError(ex, "Sales export {Start:o} - {End:o} failed mid-stream; aborting the response.", start, end);
            HttpContext.Abort();
        }

        return new EmptyResult();
    }
}
