using FluentValidation;
using MassTransit;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Consumers;

public class DinningSessionCancelledConsumer(
    IValidator<DinningSessionCancelledEvent> validator,
    PendingBillService bills,
    ILogger<DinningSessionCancelledConsumer> logger) : IConsumer<DinningSessionCancelledEvent>
{
    public async Task Consume(ConsumeContext<DinningSessionCancelledEvent> context)
    {
        await validator.ValidateAndThrowAsync(context.Message, context.CancellationToken);

        var removed = await bills.CancelSessionAsync(context.Message.DinningSessionId, context.CancellationToken);
        if (!removed)
        {
            logger.LogInformation("Sesión {SessionId} no encontrada para limpieza, se ignora", context.Message.DinningSessionId);
        }
        else
        {
            logger.LogInformation("Sesión {SessionId} eliminada de caché por anulación en comedor", context.Message.DinningSessionId);
        }
    }
}

public class DinningSessionCancelledConsumerDefinition : ConsumerDefinition<DinningSessionCancelledConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DinningSessionCancelledConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Retries are for transient failures only; invalid payloads go straight to the _error queue (DLQ).
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Interval(3, TimeSpan.FromSeconds(5));
            r.Ignore<ValidationException>();
        });
    }
}
