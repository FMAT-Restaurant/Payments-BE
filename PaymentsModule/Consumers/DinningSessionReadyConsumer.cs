using FluentValidation;
using MassTransit;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Consumers;

public class DinningSessionReadyConsumer(
    IValidator<DinningSessionReadyEvent> validator,
    CuentaPendienteService cuentas,
    ILogger<DinningSessionReadyConsumer> logger) : IConsumer<DinningSessionReadyEvent>
{
    public async Task Consume(ConsumeContext<DinningSessionReadyEvent> context)
    {
        await validator.ValidateAndThrowAsync(context.Message, context.CancellationToken);

        if (!await cuentas.GuardarSnapshotAsync(context.Message, context.CancellationToken))
        {
            logger.LogInformation("Evento {EventId} ya procesado, se omite", context.Message.EventId);
        }
    }
}

public class DinningSessionReadyConsumerDefinition : ConsumerDefinition<DinningSessionReadyConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DinningSessionReadyConsumer> consumerConfigurator,
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
