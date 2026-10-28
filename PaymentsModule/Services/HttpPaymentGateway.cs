using System.Text.Json.Serialization;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace PaymentsModule.Services;

/// <summary>
/// HTTP client of the bank gateway. Retries and the circuit breaker are applied when the client is registered
/// (see <see cref="PaymentGatewayRegistration"/>). Because a retry can repeat a POST, every call carries a stable Idempotency-Key.
/// </summary>
public class HttpPaymentGateway(HttpClient http) : IPaymentGateway
{
    public async Task<GatewayChargeResult> ChargeAsync(GatewayCharge charge, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/charges")
        {
            Content = JsonContent.Create(new ChargePayload(charge.AuthorizationReference, charge.Amount, charge.DinningSessionId))
        };
        message.Headers.Add("Idempotency-Key", IdempotencyKey("charge", charge));

        using var response = await SendAsync(message, ct);
        if (response.IsSuccessStatusCode)
        {
            return new GatewayChargeResult(true);
        }

        if (response.StatusCode is System.Net.HttpStatusCode.PaymentRequired or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            return new GatewayChargeResult(false, $"HTTP {(int)response.StatusCode}");
        }

        throw new PaymentGatewayException($"The gateway answered HTTP {(int)response.StatusCode} while charging.");
    }

    public async Task ReverseAsync(GatewayCharge charge, CancellationToken ct = default)
    {
        var path = $"api/v1/charges/{Uri.EscapeDataString(charge.AuthorizationReference)}/reversals";
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new ReversalPayload(charge.Amount, charge.DinningSessionId))
        };
        message.Headers.Add("Idempotency-Key", IdempotencyKey("reversal", charge));

        using var response = await SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PaymentGatewayException($"The gateway answered HTTP {(int)response.StatusCode} while reversing.");
        }
    }

    private static string IdempotencyKey(string operation, GatewayCharge charge) =>
        $"{operation}:{charge.DinningSessionId:N}:{charge.AuthorizationReference}";

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException)
        {
            throw new PaymentGatewayException("The payment gateway could not be reached.", ex);
        }
    }

    private record ChargePayload(
        [property: JsonPropertyName("authorization_reference")] string AuthorizationReference,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("order_reference")] Guid OrderReference);

    private record ReversalPayload(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("order_reference")] Guid OrderReference);
}
