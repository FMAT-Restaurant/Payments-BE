using System.Net;
using System.Text.Json.Serialization;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace PaymentsModule.Services.Gateway;

/// <summary>
/// Cliente HTTP de la pasarela. La resiliencia (reintentos exponenciales + circuit breaker)
/// se aplica en el registro del HttpClient (Program.cs). Como los reintentos pueden repetir un POST,
/// cada llamada lleva un Idempotency-Key estable.
/// </summary>
public sealed class PaymentGatewayClient(HttpClient httpClient) : IPaymentGatewayClient
{
    public async Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/charges")
        {
            Content = JsonContent.Create(new ChargePayload(request.AuthorizationReference, request.Amount, request.DinningSessionId)),
        };
        message.Headers.Add("Idempotency-Key", IdempotencyKey("charge", request));

        using var response = await SendAsync(message, ct);

        if (response.IsSuccessStatusCode)
        {
            return new GatewayChargeResult(true, null);
        }

        if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.UnprocessableEntity)
        {
            return new GatewayChargeResult(false, $"HTTP {(int)response.StatusCode}");
        }

        throw new PaymentGatewayException($"La pasarela respondió HTTP {(int)response.StatusCode} al cobrar.");
    }

    public async Task ReverseAsync(GatewayChargeRequest charge, CancellationToken ct)
    {
        var path = $"api/v1/charges/{Uri.EscapeDataString(charge.AuthorizationReference)}/reversals";
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new ReversalPayload(charge.Amount, charge.DinningSessionId)),
        };
        message.Headers.Add("Idempotency-Key", IdempotencyKey("reversal", charge));

        using var response = await SendAsync(message, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new PaymentGatewayException($"La pasarela respondió HTTP {(int)response.StatusCode} al extornar.");
        }
    }

    private static string IdempotencyKey(string operation, GatewayChargeRequest r) =>
        $"{operation}:{r.DinningSessionId:N}:{r.AuthorizationReference}";

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException)
        {
            throw new PaymentGatewayException("No fue posible comunicarse con la pasarela bancaria.", ex);
        }
    }

    private sealed record ChargePayload(
        [property: JsonPropertyName("authorization_reference")] string AuthorizationReference,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("order_reference")] Guid OrderReference);

    private sealed record ReversalPayload(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("order_reference")] Guid OrderReference);
}
