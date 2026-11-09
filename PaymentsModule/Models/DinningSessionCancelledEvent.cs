using System.Text.Json.Serialization;
using MassTransit;

namespace PaymentsModule.Models;

/// <summary>
/// Event emitted by Órdenes/Piso when a dining session is cancelled in the dining room (PMC-28).
/// Fixed URN so routing does not depend on the local namespace.
/// </summary>
[MessageUrn("DinningSessionCancelledEvent")]
public record DinningSessionCancelledEvent(
    [property: JsonPropertyName("eventId")] Guid EventId = default,
    [property: JsonPropertyName("dinningSessionId")] Guid DinningSessionId = default,
    DateTimeOffset Timestamp = default,
    string? Reason = null)
{
    [JsonPropertyName("event_id")]
    public Guid? EventIdSnake { init { if (value.HasValue) EventId = value.Value; } }

    [JsonPropertyName("dinning_session_id")]
    public Guid? DinningSessionIdSnake { init { if (value.HasValue) DinningSessionId = value.Value; } }
}
