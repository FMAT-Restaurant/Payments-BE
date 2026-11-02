using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PaymentsModule.Services;

public static class PaymentGatewayRegistration
{
    /// <summary>Registers the gateway client with exponential retries, a circuit breaker and a per-attempt timeout.</summary>
    public static IServiceCollection AddPaymentGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PaymentGatewayOptions.SectionName).Get<PaymentGatewayOptions>()
            ?? new PaymentGatewayOptions();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            services.AddSingleton<IPaymentGateway, UnconfiguredPaymentGateway>();
            return services;
        }

        services
            .AddHttpClient<IPaymentGateway, HttpPaymentGateway>(client =>
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"))
            .AddResilienceHandler("payment-gateway", pipeline =>
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    Delay = options.RetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true
                });

                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    FailureRatio = 0.5,
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30)
                });

                pipeline.AddTimeout(TimeSpan.FromSeconds(5));
            });

        return services;
    }
}
