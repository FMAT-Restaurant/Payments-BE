using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

/// <summary>
/// Flattened, immutable ticket for thermal printers (PMC-14). The Front-End turns it into ESC/POS commands,
/// so a reprint must always produce exactly the same document as the original.
/// </summary>
public record PrintTicketResponse(
    [property: JsonPropertyName("cabecera")] PrintTicketHeader Header,
    [property: JsonPropertyName("cuerpo")] IReadOnlyList<PrintTicketLine> Lines,
    [property: JsonPropertyName("pie")] PrintTicketFooter Footer);

/// <summary><see cref="PrintedAt"/> is the sale date, not the moment of each reprint, so every reprint is identical.</summary>
public record PrintTicketHeader(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("fecha_impresion")] DateTimeOffset PrintedAt,
    [property: JsonPropertyName("turno_caja_id")] Guid CashShiftId,
    [property: JsonPropertyName("dinning_session_id")] Guid DinningSessionId);

public record PrintTicketLine(
    [property: JsonPropertyName("nombre_platillo")] string DishName,
    [property: JsonPropertyName("cantidad")] int Quantity,
    [property: JsonPropertyName("precio_unitario")] decimal UnitPrice,
    [property: JsonPropertyName("importe")] decimal Amount);

public record PrintTicketFooter(
    [property: JsonPropertyName("subtotal")] decimal Subtotal,
    [property: JsonPropertyName("impuestos")] decimal Taxes,
    [property: JsonPropertyName("propina")] decimal Tip,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("pagos")] IReadOnlyList<PrintTicketPayment> Payments,
    [property: JsonPropertyName("estado")] TransactionStatus Status);

public record PrintTicketPayment(
    [property: JsonPropertyName("metodo_pago")] PaymentMethod Method,
    [property: JsonPropertyName("monto")] decimal Amount);
