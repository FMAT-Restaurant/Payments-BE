using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;

namespace PaymentsModule.Models;

/// <summary>
/// Configuration for integrating directly with the shared RabbitMQ topic exchange (<c>ordenes.topic</c>)
/// used by <c>orders-backend</c> via <c>RabbitMQ.Client</c> (OBS-02 / TASK-45).
/// </summary>
public sealed class OrdersTopicOptions
{
    public const string SectionName = "Messaging:OrdersTopic";
    public const string DefaultExchange = "ordenes.topic";
    public const string DefaultQueue = "cola_payments_events";
    public const string DefaultPaymentCompletedRoutingKey = "pagos.pago.completado";

    public string Exchange { get; set; } = DefaultExchange;
    public string Queue { get; set; } = DefaultQueue;
    public string[] BillRequestedRoutingKeys { get; set; } = ["ordenes.cuenta.solicitada", "ordenes.cobro.solicitado"];
    public string[] OrderCancelledRoutingKeys { get; set; } = ["ordenes.orden.cancelada", "ordenes.order.cancelled"];
    public string PaymentCompletedRoutingKey { get; set; } = DefaultPaymentCompletedRoutingKey;
}

/// <summary>
/// Raw JSON payload emitted by <c>orders-backend</c> on <c>ordenes.topic</c> under
/// <c>ordenes.cuenta.solicitada</c> and <c>ordenes.cobro.solicitado</c> (OBS-02).
/// </summary>
public record OrdersBillRequestedEvent(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("orderId")] Guid OrderId,
    [property: JsonPropertyName("sessionId")] Guid? SessionId,
    [property: JsonPropertyName("tableId")] string? TableId,
    [property: JsonPropertyName("waiterId")] string? WaiterId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("totalAmount")] decimal TotalAmount,
    [property: JsonPropertyName("items")] IReadOnlyList<OrdersBillItem> Items,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("isTakeaway")] bool? IsTakeaway = null,
    [property: JsonPropertyName("waiterName")] string? WaiterName = null,
    [property: JsonPropertyName("orderFolio")] string? OrderFolio = null)
{
    public DinningSessionReadyEvent ToDinningSessionReadyEvent()
    {
        var isTakeaway = IsTakeaway == true || SessionId is null || SessionId == Guid.Empty;
        var effectiveSessionId = SessionId is { } sid && sid != Guid.Empty ? sid : OrderId;
        var tableGuid = ParseOrDeterministicGuid(TableId, "table", "takeaway");
        var waiterGuid = ParseOrDeterministicGuid(WaiterId, "waiter", "unknown");
        var tableName = string.IsNullOrWhiteSpace(TableId) ? null : TableId.Trim();
        var waiterName = string.IsNullOrWhiteSpace(WaiterName) ? null : WaiterName.Trim();
        var orderFolio = !string.IsNullOrWhiteSpace(OrderFolio) ? OrderFolio.Trim() : OrderId.ToString();
        var timestamp = Timestamp != default ? Timestamp : DateTimeOffset.UtcNow;

        var items = Items.Select(i => new BillItem(
            DishId: ResolveDishId(i),
            Name: i.Name,
            Quantity: i.Quantity,
            UnitPrice: i.UnitPrice,
            Amount: i.Subtotal ?? (i.Quantity * i.UnitPrice),
            Notes: ResolveNotes(i))).ToList();

        return new DinningSessionReadyEvent(
            EventId: EventId,
            DinningSessionId: effectiveSessionId,
            TableId: tableGuid,
            WaiterId: waiterGuid,
            Timestamp: timestamp,
            Items: items,
            Totals: new BillTotals(Subtotal: TotalAmount, Taxes: 0m, ExpectedTotal: TotalAmount),
            TableName: tableName,
            WaiterName: waiterName,
            OrderFolio: orderFolio,
            OrderId: OrderId,
            IsTakeaway: isTakeaway);
    }

    private static Guid ResolveDishId(OrdersBillItem item)
    {
        if (Guid.TryParse(item.ItemId, out var itemGuid) && itemGuid != Guid.Empty)
        {
            return itemGuid;
        }

        if (Guid.TryParse(item.PlateId, out var plateGuid) && plateGuid != Guid.Empty)
        {
            return plateGuid;
        }

        return ParseOrDeterministicGuid(item.PlateId, "plate", item.Name);
    }

    private static string? ResolveNotes(OrdersBillItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            return item.Notes;
        }

        return item.Modifiers is { Count: > 0 } ? string.Join(", ", item.Modifiers) : null;
    }

    private static Guid ParseOrDeterministicGuid(string? raw, string scope, string fallback)
    {
        if (Guid.TryParse(raw, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        var key = $"{scope}:{(string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim())}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public record OrdersBillItem(
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("plateId")] string? PlateId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("subtotal")] decimal? Subtotal = null,
    [property: JsonPropertyName("modifiers")] IReadOnlyList<string>? Modifiers = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public class OrdersBillRequestedEventValidator : AbstractValidator<OrdersBillRequestedEvent>
{
    public OrdersBillRequestedEventValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.TotalAmount).GreaterThan(0);
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Name).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0);
            item.RuleFor(i => i.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}

/// <summary>
/// Raw JSON payload emitted by <c>orders-backend</c> on <c>ordenes.topic</c> under
/// <c>ordenes.orden.cancelada</c> / <c>ordenes.order.cancelled</c>.
/// </summary>
public record OrdersOrderCancelledEvent(
    [property: JsonPropertyName("eventId")] Guid? EventId = null,
    [property: JsonPropertyName("orderId")] Guid? OrderId = null,
    [property: JsonPropertyName("sessionId")] Guid? SessionId = null,
    [property: JsonPropertyName("dinningSessionId")] Guid? DinningSessionId = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp = null)
{
    [JsonIgnore]
    public Guid EffectiveSessionId =>
        SessionId is { } sid && sid != Guid.Empty ? sid
        : DinningSessionId is { } dsid && dsid != Guid.Empty ? dsid
        : OrderId is { } oid && oid != Guid.Empty ? oid
        : Guid.Empty;
}

public class OrdersOrderCancelledEventValidator : AbstractValidator<OrdersOrderCancelledEvent>
{
    public OrdersOrderCancelledEventValidator()
    {
        RuleFor(x => x.EffectiveSessionId).NotEmpty();
    }
}

/// <summary>
/// Raw JSON payload published by <c>Payments-BE</c> to <c>ordenes.topic</c> under routing key
/// <c>pagos.pago.completado</c> when a charge completes (OBS-02 / section 4.2).
/// </summary>
public record OrdersPaymentCompletedEvent(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("orderId")] Guid OrderId,
    [property: JsonPropertyName("sessionId")] Guid? SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("totalPaid")] decimal TotalPaid,
    [property: JsonPropertyName("montoTotal")] decimal MontoTotal,
    [property: JsonPropertyName("paymentMethod")] string PaymentMethod,
    [property: JsonPropertyName("paidAt")] DateTimeOffset PaidAt)
{
    public static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    public static OrdersPaymentCompletedEvent FromCharge(
        DinningSessionReadyEvent bill,
        PaymentTransaction transaction) => new(
        EventId: Guid.NewGuid(),
        OrderId: bill.OrderId ?? transaction.DinningSessionId,
        SessionId: bill.IsTakeaway ? null : transaction.DinningSessionId,
        Status: "PAID",
        TotalPaid: transaction.Total,
        MontoTotal: transaction.Total,
        PaymentMethod: ResolvePaymentMethod(transaction.Payments),
        PaidAt: transaction.CreatedAt);

    public static string ResolvePaymentMethod(IReadOnlyCollection<PaymentBreakdown> payments)
    {
        var hasCash = payments.Any(p => p.Method == Models.PaymentMethod.Cash);
        var hasCard = payments.Any(p => p.Method is Models.PaymentMethod.CreditCard or Models.PaymentMethod.DebitCard);
        return (hasCash, hasCard) switch
        {
            (true, true) => "MIXED",
            (false, true) => "CARD",
            _ => "CASH"
        };
    }

    public byte[] ToUtf8JsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, WireJsonOptions);
}
