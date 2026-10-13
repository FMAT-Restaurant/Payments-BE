using MassTransit;

namespace PaymentsModule.Models;

// Fixed URN so routing doesn't depend on this namespace; Órdenes must publish with the same URN.
[MessageUrn("DinningSessionReadyEvent")]
public record DinningSessionReadyEvent(
    Guid EventId,
    Guid DinningSessionId,
    Guid MesaId,
    Guid MeseroId,
    DateTimeOffset Timestamp,
    IReadOnlyList<ItemCuenta> Items,
    TotalesCuenta Totales);

public record ItemCuenta(Guid PlatilloId, string Nombre, int Cantidad, decimal PrecioUnitario, decimal Importe);

public record TotalesCuenta(decimal Subtotal, decimal Impuestos, decimal TotalEsperado);
