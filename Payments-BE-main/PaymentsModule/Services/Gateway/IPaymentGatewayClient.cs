namespace PaymentsModule.Services.Gateway;

public sealed record GatewayChargeRequest(Guid DinningSessionId, string AuthorizationReference, decimal Amount);

public sealed record GatewayChargeResult(bool Approved, string? DeclineReason);

/// <summary>Falla de infraestructura con la pasarela (red, 5xx, circuito abierto, timeout).</summary>
public sealed class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPaymentGatewayClient
{
    /// <summary>Cobra/valida la tarjeta. Un rechazo de negocio devuelve <c>Approved = false</c>; una falla técnica lanza <see cref="PaymentGatewayException"/>.</summary>
    Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct);

    /// <summary>Extorno/reversión de un cargo previamente aprobado.</summary>
    Task ReverseAsync(GatewayChargeRequest charge, CancellationToken ct);
}
