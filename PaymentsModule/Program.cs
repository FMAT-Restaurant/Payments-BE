using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using PaymentsModule.Consumers;
using PaymentsModule.Data;
using PaymentsModule.Models;
using PaymentsModule.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks();
// Error contract of every endpoint: { "code", "message" } with the message in Spanish (PMC-6, 05/11/2026).
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
    options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(
        new ErrorResponse(ErrorResponse.ValidationErrorCode, "La solicitud tiene un formato inválido.")));
ValidatorOptions.Global.LanguageManager.Culture = new CultureInfo("es");
// Built-in messages name the wire field (e.g. 'fondo_inicial') instead of the C# property.
ValidatorOptions.Global.DisplayNameResolver = (_, member, _) =>
    member?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;

// JWT forwarded by the API Gateway. Authority/audience come from Authentication:Schemes:Bearer in configuration.
builder.Services.AddAuthentication().AddJwtBearer(options =>
{
    // Auth and Kong sign HS256 with the literal shared JWT_SECRET (no base64) and issue no `aud`,
    // per the API-Gateway contract.
    var secret = builder.Configuration["Authentication:Schemes:Bearer:Secret"];
    if (!string.IsNullOrEmpty(secret))
    {
        options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        options.TokenValidationParameters.ValidateAudience = false;
    }
});
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, PaymentsModule.Infrastructure.CustomAuthorizationMiddlewareResultHandler>();

builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration.GetConnectionString("Redis"));
builder.Services.AddScoped<IValidator<DinningSessionReadyEvent>, DinningSessionReadyEventValidator>();
builder.Services.AddScoped<IValidator<DinningSessionCancelledEvent>, DinningSessionCancelledEventValidator>();
// Created on first use, so test hosts that replace the index never open a Redis connection.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));
builder.Services.AddSingleton<IPendingSessionIndex, RedisPendingSessionIndex>();
builder.Services.AddScoped<PendingBillService>();
builder.Services.AddScoped<IValidator<ChargeRequest>, ChargeRequestValidator>();
builder.Services.AddScoped<ChargeService>();
builder.Services.AddSingleton<IChargeCalculationService, ChargeCalculationService>();
builder.Services.AddScoped<IValidator<OpenCashShiftRequest>, OpenCashShiftRequestValidator>();
builder.Services.AddScoped<IValidator<CloseCashShiftRequest>, CloseCashShiftRequestValidator>();
builder.Services.AddScoped<IValidator<ForceCloseCashShiftRequest>, ForceCloseCashShiftRequestValidator>();
builder.Services.AddScoped<CashShiftService>();
builder.Services.AddScoped<TransactionDetailService>();
builder.Services.AddScoped<IValidator<SalesHistoryQuery>, SalesHistoryQueryValidator>();
builder.Services.AddScoped<SalesHistoryService>();
builder.Services.AddScoped<PrintTicketService>();
builder.Services.AddScoped<IValidator<SalesExportQuery>, SalesExportQueryValidator>();
builder.Services.AddScoped<SalesExportService>();

builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DinningSessionReadyConsumer, DinningSessionReadyConsumerDefinition>();
    x.AddConsumer<DinningSessionCancelledConsumer, DinningSessionCancelledConsumerDefinition>();
    // Bus outbox: events published from services are stored with the DbContext and only sent after COMMIT.
    x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("RabbitMq")!));
        cfg.ConfigureEndpoints(context);
    });
});

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

// Only the local container (docker-compose `api` profile) migrates on startup; other environments migrate explicitly.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

// Unhandled errors (e.g. database failures after rollback) become a generic 500 without internals.
app.UseExceptionHandler(handler => handler.Run(context =>
    context.Response.WriteAsJsonAsync(
        new ErrorResponse("INTERNAL_ERROR", "Ocurrió un error interno. Intente de nuevo más tarde."))));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

app.Run();

// Lets the HTTP integration tests boot this app with WebApplicationFactory<Program>.
public partial class Program;
