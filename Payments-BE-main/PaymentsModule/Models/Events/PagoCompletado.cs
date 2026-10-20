namespace PaymentsModule.Models.Events;

public sealed record PagoCompletado(
    Guid TransaccionId,
    string Folio,
    Guid DinningSessionId,
    decimal Total,
    DateTime FechaUtc);
