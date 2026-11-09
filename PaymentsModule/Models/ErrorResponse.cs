using System.Text.Json.Serialization;
using FluentValidation.Results;

namespace PaymentsModule.Models;

/// <summary>
/// Standard error contract agreed on 05/11/2026: { "code": "STRING_ENUM", "message": "Texto en español" }.
/// The optional <see cref="Deficit"/> field appears only for <c>INSUFFICIENT_FUNDS</c>.
/// </summary>
public record ErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("deficit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? Deficit = null)
{
    public const string ValidationErrorCode = "VALIDATION_ERROR";

    /// <summary>404 of PMC-11 and PMC-14 for an unknown folio.</summary>
    public static ErrorResponse TransactionNotFound(string folio) =>
        new("TRANSACTION_NOT_FOUND", $"El ticket '{folio}' no existe");

    /// <summary>404 of supervision summary (PMC-31 / PMC-16) for an unknown cash shift.</summary>
    public static ErrorResponse CashShiftNotFound() =>
        new("CASH_SHIFT_NOT_FOUND", "El turno de caja no existe");

    /// <summary>409 of PMC-16 when attempting to force-close an already closed shift.</summary>
    public static ErrorResponse CashShiftClosed() =>
        new("CASH_SHIFT_CLOSED", "El turno de caja ya ha sido cerrado");

    /// <summary>404 when a pending dining session is not found or already paid.</summary>
    public static ErrorResponse SessionNotFound() =>
        new("SESSION_NOT_FOUND", "La cuenta solicitada no existe o ya fue procesada.");

    /// <summary>503 when Redis cache connection fails or times out.</summary>
    public static ErrorResponse RedisTimeout() =>
        new("REDIS_TIMEOUT", "El servicio de caché no se encuentra disponible temporalmente.");

    /// <summary>400 body: every validation message joined in one Spanish sentence list.</summary>
    public static ErrorResponse Validation(ValidationResult result) =>
        new(ValidationErrorCode, string.Join(" ", result.Errors.Select(e => e.ErrorMessage).Distinct()));
}
