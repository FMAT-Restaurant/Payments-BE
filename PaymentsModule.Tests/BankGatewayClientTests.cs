using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// No database: these cover the configurable mock and the timeout of the bank gateway client (PMC-12-P2, P5).
public class BankGatewayClientTests
{
    private static BankGatewayOptions MockOptions(int statusCode) => new()
    {
        UseMock = true,
        MockStatusCode = statusCode,
        RetryDelay = TimeSpan.FromMilliseconds(1),
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static BankRefundRequest Refund() => new("TX-20261023-000001", "AUTH-1", 100m, "key-1");

    [Fact]
    public async Task Mock_answering_200_confirms_the_refund_with_a_reference()
    {
        using var provider = new ServiceCollection().AddBankGateway(MockOptions(StatusCodes.Status200OK)).BuildServiceProvider();

        var result = await provider.GetRequiredService<IBankGatewayClient>().RefundAsync(Refund());

        Assert.StartsWith("MOCK-REFUND-", result.RefundReference);
    }

    [Fact]
    public async Task Mock_answering_503_exhausts_the_retry_policy_and_fails()
    {
        using var provider = new ServiceCollection().AddBankGateway(MockOptions(StatusCodes.Status503ServiceUnavailable)).BuildServiceProvider();

        await Assert.ThrowsAsync<BankGatewayException>(() => provider.GetRequiredService<IBankGatewayClient>().RefundAsync(Refund()));
    }

    [Fact]
    public async Task Mock_answering_a_client_error_fails()
    {
        using var provider = new ServiceCollection().AddBankGateway(MockOptions(StatusCodes.Status400BadRequest)).BuildServiceProvider();

        await Assert.ThrowsAsync<BankGatewayException>(() => provider.GetRequiredService<IBankGatewayClient>().RefundAsync(Refund()));
    }

    [Fact]
    public async Task A_hanging_gateway_hits_the_timeout_and_fails()
    {
        var options = new BankGatewayOptions { UseMock = false, BaseUrl = "http://bank.test/", Timeout = TimeSpan.FromMilliseconds(300) };
        var services = new ServiceCollection();
        services.AddHttpClient<IBankGatewayClient, BankGatewayClient>(http => http.BaseAddress = new Uri(options.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler())
            .AddBankGatewayResilience(options);
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<BankGatewayException>(() => provider.GetRequiredService<IBankGatewayClient>().RefundAsync(Refund()));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
