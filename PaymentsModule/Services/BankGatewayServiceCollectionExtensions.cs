using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PaymentsModule.Services;

public static class BankGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddBankGateway(this IServiceCollection services, IConfiguration configuration) =>
        services.AddBankGateway(configuration.GetSection(BankGatewayOptions.SectionName).Get<BankGatewayOptions>() ?? new BankGatewayOptions());

    public static IServiceCollection AddBankGateway(this IServiceCollection services, BankGatewayOptions options)
    {
        services.AddSingleton(options);
        services.AddTransient<MockBankGatewayHandler>();
        services.AddHttpClient<IBankGatewayClient, BankGatewayClient>(http => http.BaseAddress = new Uri(options.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(sp => options.UseMock
                ? (HttpMessageHandler)sp.GetRequiredService<MockBankGatewayHandler>()
                : new SocketsHttpHandler())
            .AddBankGatewayResilience(options);
        return services;
    }

    /// <summary>Total timeout around exponential-backoff retries. Retries cover 5xx, 408, 429 and network errors; other 4xx are final.</summary>
    public static IHttpClientBuilder AddBankGatewayResilience(this IHttpClientBuilder client, BankGatewayOptions options)
    {
        client.AddResilienceHandler("bank-gateway", pipeline =>
        {
            // Outermost: the whole call, retries included, must fit in this budget.
            pipeline.AddTimeout(options.Timeout);
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.RetryCount,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.RetryDelay,
                UseJitter = true
            });
        });
        return client;
    }
}
