using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Options;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public enum OrdersMessageOutcome
{
    Ack,
    Reject
}

/// <summary>
/// Routes and processes raw JSON messages received from <c>ordenes.topic</c> by AMQP routing key.
/// Decoupled from the RabbitMQ transport so it can be unit-tested directly.
/// </summary>
public partial class OrdersMessageHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<OrdersTopicOptions> options,
    ILogger<OrdersMessageHandler> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<OrdersMessageOutcome> HandleAsync(
        string routingKey,
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default)
    {
        var cfg = options.Value;

        if (cfg.BillRequestedRoutingKeys.Contains(routingKey, StringComparer.OrdinalIgnoreCase))
        {
            return await HandleBillRequestedAsync(routingKey, body, ct);
        }

        if (cfg.OrderCancelledRoutingKeys.Contains(routingKey, StringComparer.OrdinalIgnoreCase))
        {
            return await HandleOrderCancelledAsync(routingKey, body, ct);
        }

        LogUnmappedRoutingKey(logger, routingKey);
        return OrdersMessageOutcome.Ack;
    }

    private async Task<OrdersMessageOutcome> HandleBillRequestedAsync(
        string routingKey,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        OrdersBillRequestedEvent? message;
        try
        {
            message = JsonSerializer.Deserialize<OrdersBillRequestedEvent>(body.Span, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogInvalidJson(logger, ex, routingKey);
            return OrdersMessageOutcome.Reject;
        }

        if (message is null)
        {
            LogNullPayload(logger, routingKey);
            return OrdersMessageOutcome.Reject;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var validator = scope.ServiceProvider.GetRequiredService<IValidator<OrdersBillRequestedEvent>>();
        var validation = await validator.ValidateAsync(message, ct);
        if (!validation.IsValid)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                var errors = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
                LogBillValidationFailed(logger, routingKey, message.EventId, message.OrderId, errors);
            }

            return OrdersMessageOutcome.Reject;
        }

        var bills = scope.ServiceProvider.GetRequiredService<PendingBillService>();
        var mapped = message.ToDinningSessionReadyEvent();
        if (!await bills.SaveSnapshotAsync(mapped, ct))
        {
            LogBillSkipped(logger, message.EventId, message.OrderId, mapped.DinningSessionId);
        }
        else
        {
            LogBillSaved(logger, routingKey, message.OrderId, mapped.DinningSessionId, message.Status, message.TotalAmount);
        }

        return OrdersMessageOutcome.Ack;
    }

    private async Task<OrdersMessageOutcome> HandleOrderCancelledAsync(
        string routingKey,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        OrdersOrderCancelledEvent? message;
        try
        {
            message = JsonSerializer.Deserialize<OrdersOrderCancelledEvent>(body.Span, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogInvalidJson(logger, ex, routingKey);
            return OrdersMessageOutcome.Reject;
        }

        if (message is null)
        {
            LogNullPayload(logger, routingKey);
            return OrdersMessageOutcome.Reject;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var validator = scope.ServiceProvider.GetRequiredService<IValidator<OrdersOrderCancelledEvent>>();
        var validation = await validator.ValidateAsync(message, ct);
        if (!validation.IsValid)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                var errors = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
                LogCancellationValidationFailed(logger, routingKey, errors);
            }

            return OrdersMessageOutcome.Reject;
        }

        var bills = scope.ServiceProvider.GetRequiredService<PendingBillService>();
        var sessionId = message.EffectiveSessionId;
        var removed = await bills.CancelSessionAsync(sessionId, ct);
        if (!removed)
        {
            LogCancellationTombstoneOnly(logger, routingKey, sessionId, message.OrderId);
        }
        else
        {
            LogCancellationRemoved(logger, routingKey, sessionId, message.OrderId);
        }

        return OrdersMessageOutcome.Ack;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring message with unmapped routing key '{RoutingKey}'")]
    private static partial void LogUnmappedRoutingKey(ILogger logger, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid JSON payload on '{RoutingKey}'; rejecting without requeue")]
    private static partial void LogInvalidJson(ILogger logger, Exception ex, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Null payload on '{RoutingKey}'; rejecting without requeue")]
    private static partial void LogNullPayload(ILogger logger, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validation failed for '{RoutingKey}' (event {EventId}, order {OrderId}): {Errors}")]
    private static partial void LogBillValidationFailed(ILogger logger, string routingKey, Guid eventId, Guid orderId, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orders event {EventId} for order {OrderId} (session {SessionId}) already processed, outdated, or closed; skipping")]
    private static partial void LogBillSkipped(ILogger logger, Guid eventId, Guid orderId, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved pending bill from '{RoutingKey}': order {OrderId}, session {SessionId}, status {Status}, total {Total}")]
    private static partial void LogBillSaved(ILogger logger, string routingKey, Guid orderId, Guid sessionId, string? status, decimal total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validation failed for cancellation on '{RoutingKey}': {Errors}")]
    private static partial void LogCancellationValidationFailed(ILogger logger, string routingKey, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orders cancellation on '{RoutingKey}' for session {SessionId} (order {OrderId}): not in cache, tombstone stored")]
    private static partial void LogCancellationTombstoneOnly(ILogger logger, string routingKey, Guid sessionId, Guid? orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Orders cancellation on '{RoutingKey}' for session {SessionId} (order {OrderId}) removed from pending bills cache")]
    private static partial void LogCancellationRemoved(ILogger logger, string routingKey, Guid sessionId, Guid? orderId);
}
