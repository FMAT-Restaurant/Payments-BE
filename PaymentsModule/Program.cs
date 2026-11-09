using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();

builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration.GetConnectionString("Redis"));
builder.Services.AddScoped<IValidator<DinningSessionReadyEvent>, DinningSessionReadyEventValidator>();
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

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

app.Run();

// Lets the HTTP integration tests boot this app with WebApplicationFactory<Program>.
public partial class Program;
