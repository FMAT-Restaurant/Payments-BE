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
public class OrdersMessageHandler(
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

        logger.LogWarning("Ignoring message with unmapped routing key '{RoutingKey}'", routingKey);
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
            logger.LogWarning(ex, "Invalid JSON payload on '{RoutingKey}'; rejecting without requeue", routingKey);
            return OrdersMessageOutcome.Reject;
        }

        if (message is null)
        {
            logger.LogWarning("Null payload on '{RoutingKey}'; rejecting without requeue", routingKey);
            return OrdersMessageOutcome.Reject;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var validator = scope.ServiceProvider.GetRequiredService<IValidator<OrdersBillRequestedEvent>>();
        var validation = await validator.ValidateAsync(message, ct);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "Validation failed for '{RoutingKey}' (event {EventId}, order {OrderId}): {Errors}",
                routingKey,
                message.EventId,
                message.OrderId,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
            return OrdersMessageOutcome.Reject;
        }

        var bills = scope.ServiceProvider.GetRequiredService<PendingBillService>();
        var mapped = message.ToDinningSessionReadyEvent();
        if (!await bills.SaveSnapshotAsync(mapped, ct))
        {
            logger.LogInformation(
                "Orders event {EventId} for order {OrderId} (session {SessionId}) already processed, outdated, or closed; skipping",
                message.EventId,
                message.OrderId,
                mapped.DinningSessionId);
        }
        else
        {
            logger.LogInformation(
                "Saved pending bill from '{RoutingKey}': order {OrderId}, session {SessionId}, status {Status}, total {Total}",
                routingKey,
                message.OrderId,
                mapped.DinningSessionId,
                message.Status,
                message.TotalAmount);
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
            logger.LogWarning(ex, "Invalid JSON payload on '{RoutingKey}'; rejecting without requeue", routingKey);
            return OrdersMessageOutcome.Reject;
        }

        if (message is null)
        {
            logger.LogWarning("Null payload on '{RoutingKey}'; rejecting without requeue", routingKey);
            return OrdersMessageOutcome.Reject;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var validator = scope.ServiceProvider.GetRequiredService<IValidator<OrdersOrderCancelledEvent>>();
        var validation = await validator.ValidateAsync(message, ct);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "Validation failed for cancellation on '{RoutingKey}': {Errors}",
                routingKey,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
            return OrdersMessageOutcome.Reject;
        }

        var bills = scope.ServiceProvider.GetRequiredService<PendingBillService>();
        var sessionId = message.EffectiveSessionId;
        var removed = await bills.CancelSessionAsync(sessionId, ct);
        if (!removed)
        {
            logger.LogInformation(
                "Orders cancellation on '{RoutingKey}' for session {SessionId} (order {OrderId}): not in cache, tombstone stored",
                routingKey,
                sessionId,
                message.OrderId);
        }
        else
        {
            logger.LogInformation(
                "Orders cancellation on '{RoutingKey}' for session {SessionId} (order {OrderId}) removed from pending bills cache",
                routingKey,
                sessionId,
                message.OrderId);
        }

        return OrdersMessageOutcome.Ack;
    }
}
