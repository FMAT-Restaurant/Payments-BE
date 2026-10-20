using System.Text.Json.Serialization;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PaymentsModule.Filters;
using PaymentsModule.Persistence;
using PaymentsModule.Services;
using PaymentsModule.Services.Gateway;
using PaymentsModule.Validators;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks();

// Seguridad: JWT emitido por el API Gateway / IdP. El cajero se obtiene de los claims.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        builder.Configuration.GetSection("Authentication:Jwt").Bind(options);
        options.MapInboundClaims = false; // conserva el claim "sub" tal cual
    });
builder.Services.AddAuthorization();

// Persistencia (EF Core + SQL Server).
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("PaymentsDb")));

// Mensajería: MassTransit con Transactional Outbox acoplado al DbContext.
builder.Services.AddMassTransit(bus =>
{
    bus.AddEntityFrameworkOutbox<PaymentsDbContext>(outbox =>
    {
        outbox.UseSqlServer();
        outbox.UseBusOutbox();
    });

    var rabbitHost = builder.Configuration["RabbitMq:Host"];
    if (string.IsNullOrWhiteSpace(rabbitHost))
    {
        // Solo desarrollo: sin broker configurado se usa transporte en memoria.
        bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
    }
    else
    {
        bus.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(rabbitHost, "/", host =>
            {
                host.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
                host.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
            });
            cfg.ConfigureEndpoints(context);
        });
    }
});

// Pasarela bancaria con Polly: reintentos exponenciales + circuit breaker.
builder.Services.AddOptions<PaymentGatewayOptions>()
    .Bind(builder.Configuration.GetSection(PaymentGatewayOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<IPaymentGatewayClient, PaymentGatewayClient>((sp, client) =>
    {
        var gateway = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PaymentGatewayOptions>>().Value;
        client.BaseAddress = new Uri(gateway.BaseUrl.TrimEnd('/') + "/");
    })
    .AddResilienceHandler("payment-gateway", pipeline =>
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(300),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        });

        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        pipeline.AddTimeout(TimeSpan.FromSeconds(5));
    });

// Dominio / servicios.
builder.Services.AddOptions<CobroOptions>().Bind(builder.Configuration.GetSection(CobroOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddValidatorsFromAssemblyContaining<CobroRequestDTOValidator>();
builder.Services.AddScoped<CobroRequestValidationFilter>();
builder.Services.AddScoped(typeof(ValidationFilter<>));
builder.Services.AddScoped<ICobroService, CobroService>();
builder.Services.AddScoped<ITransaccionQueryService, TransaccionQueryService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICajeroContext, HttpCajeroContext>();
builder.Services.AddScoped<ICajaService, CajaService>();

// Observability (from main's ServiceDefaults). Exports only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

var otel = builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(tracing => tracing
        .AddSource(builder.Environment.ApplicationName)
        .AddAspNetCoreInstrumentation(options =>
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
