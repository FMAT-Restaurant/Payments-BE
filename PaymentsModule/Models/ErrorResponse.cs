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

    /// <summary>400 body: every validation message joined in one Spanish sentence list.</summary>
    public static ErrorResponse Validation(ValidationResult result) =>
        new(ValidationErrorCode, string.Join(" ", result.Errors.Select(e => e.ErrorMessage).Distinct()));
}
