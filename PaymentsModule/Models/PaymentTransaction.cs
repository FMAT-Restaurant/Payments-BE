using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

// Immutable sale history. Items are a plain-text snapshot: no references to the product catalog.
public class PaymentTransaction
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public required string Folio { get; set; }
    public Guid DinningSessionId { get; set; }
    public Guid CashShiftId { get; set; }
    public required string CashierId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Taxes { get; set; }
    public decimal Tip { get; set; }

    /// <summary>Subtotal + taxes + tip. Always equals the sum of <see cref="Payments"/>.</summary>
    public decimal Total { get; set; }

    public TransactionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<TransactionItem> Items { get; set; } = [];
    public List<PaymentBreakdown> Payments { get; set; } = [];
}

public class TransactionItem
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public required string DishName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

public class PaymentBreakdown
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public PaymentMethod Method { get; set; }

    /// <summary>Amount applied to the bill; cash change is never stored as income.</summary>
    public decimal Amount { get; set; }

    public string? AuthorizationReference { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TransactionStatus>))]
public enum TransactionStatus
{
    [JsonStringEnumMemberName("COMPLETADO")] Completed,
    [JsonStringEnumMemberName("CANCELADO")] Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<PaymentMethod>))]
public enum PaymentMethod
{
    [JsonStringEnumMemberName("EFECTIVO")] Cash,
    [JsonStringEnumMemberName("TARJETA_CREDITO")] CreditCard,
    [JsonStringEnumMemberName("TARJETA_DEBITO")] DebitCard
}
