using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// No database: the real HttpPaymentGateway and its Polly pipeline run against a stubbed HTTP handler.
public class PaymentGatewayTests
{
    private static readonly GatewayCharge Charge = new(Guid.Parse("a1b2c3d4-e5f6-7890-1234-56789abcdef0"), "AUTH-1", 150m);

    [Fact]
    public async Task Approves_a_charge_and_sends_a_stable_idempotency_key()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK);

        var result = await gateway.ChargeAsync(Charge);

        Assert.True(result.Approved);
        var seen = Assert.Single(handler.Seen);
        Assert.Equal("/api/v1/charges", seen.Path);
        Assert.Equal("charge:a1b2c3d4e5f67890123456789abcdef0:AUTH-1", seen.IdempotencyKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Reports_a_decline_without_throwing_or_retrying(HttpStatusCode status)
    {
        var (gateway, handler) = Create(status);

        var result = await gateway.ChargeAsync(Charge);

        Assert.False(result.Approved);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retries_transient_failures_reusing_the_same_idempotency_key()
    {
        var (gateway, handler) = Create(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var result = await gateway.ChargeAsync(Charge);

        Assert.True(result.Approved);
        Assert.Equal(3, handler.Calls);
        Assert.Single(handler.Seen.Select(s => s.IdempotencyKey).Distinct());
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries_as_unavailable()
    {
        var (gateway, handler) = Create(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.ChargeAsync(Charge));

        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task Opens_the_circuit_after_repeated_failures_and_stops_calling_the_gateway()
    {
        var (gateway, handler) = Create(HttpStatusCode.ServiceUnavailable);
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.ChargeAsync(Charge));
        }

        var callsBefore = handler.Calls;
        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.ChargeAsync(Charge));

        Assert.Equal(callsBefore, handler.Calls);
    }

    [Fact]
    public async Task Reverses_a_charge_through_the_reversals_endpoint()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK);

        await gateway.ReverseAsync(Charge);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal("/api/v1/charges/AUTH-1/reversals", seen.Path);
        Assert.StartsWith("reversal:", seen.IdempotencyKey);
    }

    [Fact]
    public async Task A_failed_reversal_is_reported_as_a_gateway_failure()
    {
        var (gateway, _) = Create(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.ReverseAsync(Charge));
    }

    [Fact]
    public async Task Without_a_base_url_no_gateway_is_called_and_cards_are_accepted()
    {
        var services = new ServiceCollection().AddLogging().AddPaymentGateway(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var gateway = provider.GetRequiredService<IPaymentGateway>();

        Assert.IsType<UnconfiguredPaymentGateway>(gateway);
        Assert.True((await gateway.ChargeAsync(Charge)).Approved);
        await gateway.ReverseAsync(Charge);
    }

    private static (IPaymentGateway Gateway, StubHandler Handler) Create(params HttpStatusCode[] responses)
    {
        var handler = new StubHandler(responses);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PaymentGateway:BaseUrl"] = "https://gateway.test",
                ["PaymentGateway:RetryDelay"] = "00:00:00.001",
                ["PaymentGateway:MaxRetryAttempts"] = "3"
            })
            .Build();

        var services = new ServiceCollection().AddLogging().AddPaymentGateway(configuration);
        // The typed client is named after its service type; swap its network handler for the stub.
        services.AddHttpClient(nameof(IPaymentGateway)).ConfigurePrimaryHttpMessageHandler(() => handler);
        return (services.BuildServiceProvider().GetRequiredService<IPaymentGateway>(), handler);
    }

    private sealed class StubHandler(HttpStatusCode[] responses) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<(string Path, string? IdempotencyKey)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            lock (Seen)
            {
                Seen.Add((request.RequestUri!.AbsolutePath, key));
            }

            var index = Math.Min(Interlocked.Increment(ref _calls) - 1, responses.Length - 1);
            return Task.FromResult(new HttpResponseMessage(responses[index]));
        }
    }
}
