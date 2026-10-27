using MassTransit;

namespace PaymentsModule.Models;

// Fixed URN so Órdenes and Cocina can bind to it regardless of this namespace (same approach as PMC-5).
[MessageUrn("PaymentCompletedEvent")]
public record PaymentCompletedEvent(
    Guid EventId,
    Guid DinningSessionId,
    string Folio,
    decimal Total,
    DateTimeOffset Timestamp);
