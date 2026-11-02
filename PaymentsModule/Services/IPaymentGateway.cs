namespace PaymentsModule.Services;

/// <summary>A card payment sent to the bank gateway. The pair session + reference doubles as the idempotency key.</summary>
public record GatewayCharge(Guid DinningSessionId, string AuthorizationReference, decimal Amount);

public record GatewayChargeResult(bool Approved, string? DeclineReason = null);

/// <summary>Infrastructure failure talking to the gateway (network, 5xx, open circuit, timeout). A decline is not an exception.</summary>
public class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPaymentGateway
{
    /// <summary>Charges a card. A business decline returns <c>Approved = false</c>; a technical failure throws <see cref="PaymentGatewayException"/>.</summary>
    Task<GatewayChargeResult> ChargeAsync(GatewayCharge charge, CancellationToken ct = default);

    /// <summary>Reverses a charge that was approved earlier (automatic refund).</summary>
    Task ReverseAsync(GatewayCharge charge, CancellationToken ct = default);
}
