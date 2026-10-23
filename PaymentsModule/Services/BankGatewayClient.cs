using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;

namespace PaymentsModule.Services;

public record BankRefundRequest(string Folio, string AuthorizationReference, decimal Amount, string IdempotencyKey);

public record BankRefundResult(string RefundReference);

/// <summary>The gateway could not refund the money (outage, timeout, rejection or an unusable answer).</summary>
public class BankGatewayException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IBankGatewayClient
{
    /// <summary>Refunds one card payment. Throws <see cref="BankGatewayException"/> when the refund did not happen.</summary>
    Task<BankRefundResult> RefundAsync(BankRefundRequest request, CancellationToken ct = default);
}

// Provisional wire contract: the real gateway spec is still pending (PMC-12-P2).
public record BankRefundHttpRequest(
    [property: JsonPropertyName("folio")] string Folio,
    [property: JsonPropertyName("referencia_autorizacion")] string AuthorizationReference,
    [property: JsonPropertyName("monto")] decimal Amount);

public record BankRefundHttpResponse(
    [property: JsonPropertyName("referencia_extorno")] string? RefundReference);

public class BankGatewayClient(HttpClient http) : IBankGatewayClient
{
    public async Task<BankRefundResult> RefundAsync(BankRefundRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "refunds")
        {
            Content = JsonContent.Create(new BankRefundHttpRequest(request.Folio, request.AuthorizationReference, request.Amount))
        };
        // Lets the gateway recognise a repeated refund (e.g. a retry after a failed COMMIT) and not refund twice.
        message.Headers.Add("Idempotency-Key", request.IdempotencyKey);

        try
        {
            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new BankGatewayException($"The bank gateway answered {(int)response.StatusCode} to the refund.");
            }

            var body = await response.Content.ReadFromJsonAsync<BankRefundHttpResponse>(ct);
            if (string.IsNullOrWhiteSpace(body?.RefundReference))
            {
                throw new BankGatewayException("The bank gateway confirmed the refund without a reference.");
            }

            return new BankRefundResult(body.RefundReference);
        }
        // Retries are exhausted or the time budget is spent. A caller that cancelled the request is not a gateway failure.
        catch (Exception ex) when ((ex is HttpRequestException or TimeoutRejectedException or OperationCanceledException or JsonException)
            && !ct.IsCancellationRequested)
        {
            throw new BankGatewayException("The bank gateway could not be reached.", ex);
        }
    }
}
