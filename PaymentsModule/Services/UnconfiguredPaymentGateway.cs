namespace PaymentsModule.Services;

/// <summary>
/// Used while <c>PaymentGateway:BaseUrl</c> is empty: card payments keep working as before this gateway existed
/// (the authorization reference comes from the card terminal) and every call is logged as a warning.
/// </summary>
public class UnconfiguredPaymentGateway(ILogger<UnconfiguredPaymentGateway> logger) : IPaymentGateway
{
    public Task<GatewayChargeResult> ChargeAsync(GatewayCharge charge, CancellationToken ct = default)
    {
        logger.LogWarning(
            "PaymentGateway:BaseUrl is not configured; card payment {Reference} was accepted without gateway validation.",
            charge.AuthorizationReference);
        return Task.FromResult(new GatewayChargeResult(true));
    }

    public Task ReverseAsync(GatewayCharge charge, CancellationToken ct = default)
    {
        logger.LogWarning(
            "PaymentGateway:BaseUrl is not configured; reversal of card payment {Reference} must be done manually.",
            charge.AuthorizationReference);
        return Task.CompletedTask;
    }
}
