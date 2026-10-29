using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

/// <summary>Immutable audit record of a cancelled sale. The cancelled <see cref="PaymentTransaction"/> is never deleted.</summary>
public class SaleCancellation
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }

    /// <summary>Manager or administrator who authorized the cancellation, taken from the JWT.</summary>
    public required string AuthorizerId { get; set; }

    public required string Reason { get; set; }
    public DateTimeOffset CancelledAt { get; set; }

    /// <summary>Refund references returned by the bank gateway, comma separated. <c>null</c> when nothing went through the gateway.</summary>
    public string? GatewayReference { get; set; }

    /// <summary>Total refunded through the bank gateway (card payments only). Cash is never refunded through the gateway.</summary>
    public decimal RefundedAmount { get; set; }
}

// Wire contract of PMC-12 keeps the Spanish snake_case names.
public record CancelSaleRequest(
    [property: JsonPropertyName("motivo")] string? Reason);

public record CancelSaleResponse(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("estado")] TransactionStatus Status,
    [property: JsonPropertyName("fecha_cancelacion")] DateTimeOffset CancelledAt,
    [property: JsonPropertyName("mensaje")] string Message);
