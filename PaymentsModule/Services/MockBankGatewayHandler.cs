using System.Net;

namespace PaymentsModule.Services;

/// <summary>Stands in for the bank gateway while none is deployed: answers the status set in <see cref="BankGatewayOptions.MockStatusCode"/>.</summary>
public sealed class MockBankGatewayHandler(BankGatewayOptions options) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage((HttpStatusCode)options.MockStatusCode) { RequestMessage = request };
        if (response.IsSuccessStatusCode)
        {
            response.Content = JsonContent.Create(new BankRefundHttpResponse($"MOCK-REFUND-{Guid.NewGuid():N}"));
        }

        return Task.FromResult(response);
    }
}
