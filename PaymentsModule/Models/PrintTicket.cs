using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

/// <summary>
/// Flattened, immutable ticket ready to print from the browser (PMC-14).
/// Any reprint must be identical to the original ticket.
/// </summary>
public record PrintTicketResponse(
    [property: JsonPropertyName("restaurant_name")] string RestaurantName,
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("paid_at")] DateTimeOffset PaidAt,
    [property: JsonPropertyName("table_name")] string TableName,
    [property: JsonPropertyName("cashier_name")] string CashierName,
    [property: JsonPropertyName("items")] IReadOnlyList<PrintTicketLine> Items,
    [property: JsonPropertyName("subtotal")] decimal Subtotal,
    [property: JsonPropertyName("taxes")] decimal Taxes,
    [property: JsonPropertyName("tip")] decimal Tip,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("payments")] IReadOnlyList<PrintTicketPayment> Payments,
    [property: JsonPropertyName("change_given")] decimal ChangeGiven);

public record PrintTicketLine(
    [property: JsonPropertyName("dish_name")] string DishName,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unit_price")] decimal UnitPrice,
    [property: JsonPropertyName("amount")] decimal Amount);

public record PrintTicketPayment(
    [property: JsonPropertyName("method")] PaymentMethod Method,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("authorization_reference")] string? AuthorizationReference);
