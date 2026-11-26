using FluentValidation;
using MassTransit;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Consumers;

/// <summary>
/// Consumes raw JSON bill/charge requests published by <c>orders-backend</c> on <c>ordenes.topic</c>
/// (<c>ordenes.cuenta.solicitada</c> and <c>ordenes.cobro.solicitado</c>) and stores them in the
/// standard pending-bill snapshot pipeline (OBS-02 / TASK-45).
/// </summary>
public class OrdersBillRequestedConsumer(
    IValidator<OrdersBillRequestedEvent> validator,
    PendingBillService bills,
    ILogger<OrdersBillRequestedConsumer> logger) : IConsumer<OrdersBillRequestedEvent>
{
    public async Task Consume(ConsumeContext<OrdersBillRequestedEvent> context)
    {
        await validator.ValidateAndThrowAsync(context.Message, context.CancellationToken);

        var mapped = context.Message.ToDinningSessionReadyEvent();
        if (!await bills.SaveSnapshotAsync(mapped, context.CancellationToken))
        {
            logger.LogInformation(
                "Orders event {EventId} for order {OrderId} (session {SessionId}) already processed, outdated, or closed; skipping",
                context.Message.EventId,
                context.Message.OrderId,
                mapped.DinningSessionId);
        }
        else
        {
            logger.LogInformation(
                "Saved pending bill from ordenes.topic: order {OrderId}, session {SessionId}, status {Status}, total {Total}",
                context.Message.OrderId,
                mapped.DinningSessionId,
                context.Message.Status,
                context.Message.TotalAmount);
        }
    }
}

public class OrdersBillRequestedConsumerDefinition : ConsumerDefinition<OrdersBillRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<OrdersBillRequestedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Interval(3, TimeSpan.FromSeconds(5));
            r.Ignore<ValidationException>();
        });
    }
}

/// <summary>
/// Consumes raw JSON order cancellations published by <c>orders-backend</c> on <c>ordenes.topic</c>
/// (<c>ordenes.orden.cancelada</c> / <c>ordenes.order.cancelled</c>) and removes any pending bill
/// from Redis with a tombstone (PMC-28 / OBS-02).
/// </summary>
public class OrdersOrderCancelledConsumer(
    IValidator<OrdersOrderCancelledEvent> validator,
    PendingBillService bills,
    ILogger<OrdersOrderCancelledConsumer> logger) : IConsumer<OrdersOrderCancelledEvent>
{
    public async Task Consume(ConsumeContext<OrdersOrderCancelledEvent> context)
    {
        await validator.ValidateAndThrowAsync(context.Message, context.CancellationToken);

        var sessionId = context.Message.EffectiveSessionId;
        var removed = await bills.CancelSessionAsync(sessionId, context.CancellationToken);
        if (!removed)
        {
            logger.LogInformation(
                "Orders cancellation for session {SessionId} (order {OrderId}): not found in cache, tombstone stored",
                sessionId,
                context.Message.OrderId);
        }
        else
        {
            logger.LogInformation(
                "Orders cancellation for session {SessionId} (order {OrderId}) removed from pending bills cache",
                sessionId,
                context.Message.OrderId);
        }
    }
}

public class OrdersOrderCancelledConsumerDefinition : ConsumerDefinition<OrdersOrderCancelledConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<OrdersOrderCancelledConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Interval(3, TimeSpan.FromSeconds(5));
            r.Ignore<ValidationException>();
        });
    }
}
