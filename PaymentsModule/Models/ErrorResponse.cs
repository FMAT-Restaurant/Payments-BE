using System.Text.Json.Serialization;

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
    decimal? Deficit = null);
