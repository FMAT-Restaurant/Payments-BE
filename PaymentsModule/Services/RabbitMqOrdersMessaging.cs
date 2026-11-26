using Microsoft.Extensions.Options;
using PaymentsModule.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace PaymentsModule.Services;

/// <summary>
/// Publishes raw JSON payment events to <c>ordenes.topic</c> without creating extra exchanges.
/// </summary>
public interface IOrdersEventPublisher
{
    Task PublishPaymentCompletedAsync(OrdersPaymentCompletedEvent message, CancellationToken ct = default);
}

public sealed class NoOpOrdersEventPublisher : IOrdersEventPublisher
{
    public Task PublishPaymentCompletedAsync(OrdersPaymentCompletedEvent message, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Maintains a single shared, auto-recovering RabbitMQ.Client connection to the broker.
/// </summary>
public sealed class RabbitMqConnectionManager(IConfiguration configuration) : IAsyncDisposable
{
    private readonly string _connectionString = configuration.GetConnectionString("RabbitMq")
        ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is required.");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_connectionString),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = "Payments-BE"
            };

            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
    }
}

/// <summary>
/// Publishes <c>pagos.pago.completado</c> directly to <c>ordenes.topic</c> as plain UTF-8 JSON.
/// </summary>
public sealed class RabbitMqOrdersEventPublisher(
    RabbitMqConnectionManager connectionManager,
    IOptions<OrdersTopicOptions> options,
    ILogger<RabbitMqOrdersEventPublisher> logger) : IOrdersEventPublisher
{
    public async Task PublishPaymentCompletedAsync(OrdersPaymentCompletedEvent message, CancellationToken ct = default)
    {
        var cfg = options.Value;
        var connection = await connectionManager.GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.EventId.ToString(),
            Timestamp = new AmqpTimestamp(message.PaidAt.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(
            exchange: cfg.Exchange,
            routingKey: cfg.PaymentCompletedRoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: message.ToUtf8JsonBytes(),
            cancellationToken: ct);

        logger.LogInformation(
            "Published '{RoutingKey}' to '{Exchange}' for order {OrderId} (session {SessionId}, total {Total})",
            cfg.PaymentCompletedRoutingKey,
            cfg.Exchange,
            message.OrderId,
            message.SessionId,
            message.TotalPaid);
    }
}

/// <summary>
/// Consumes <c>ordenes.topic</c> events via <c>RabbitMQ.Client</c> using a single dedicated queue
/// (<c>cola_payments_events</c>) bound directly to <c>ordenes.topic</c>, creating zero extra exchanges.
/// </summary>
public sealed class RabbitMqOrdersConsumerService(
    RabbitMqConnectionManager connectionManager,
    OrdersMessageHandler handler,
    IOptions<OrdersTopicOptions> options,
    ILogger<RabbitMqOrdersConsumerService> logger) : BackgroundService
{
    private const int MaxTransientAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        var connection = await connectionManager.GetConnectionAsync(stoppingToken);
        await EnsureTopicExchangeExistsAsync(connection, cfg.Exchange, stoppingToken);

        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 20, global: false, cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: cfg.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        var routingKeys = cfg.BillRequestedRoutingKeys
            .Concat(cfg.OrderCancelledRoutingKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var routingKey in routingKeys)
        {
            await channel.QueueBindAsync(
                queue: cfg.Queue,
                exchange: cfg.Exchange,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: stoppingToken);
        }

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            await ProcessDeliveryAsync(channel, ea, stoppingToken);
        };

        await channel.BasicConsumeAsync(
            queue: cfg.Queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "RabbitMQ.Client connected: queue '{Queue}' bound directly to '{Exchange}' for [{RoutingKeys}]",
            cfg.Queue,
            cfg.Exchange,
            string.Join(", ", routingKeys));

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private async Task ProcessDeliveryAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            try
            {
                var outcome = await handler.HandleAsync(ea.RoutingKey, ea.Body, ct);
                if (outcome == OrdersMessageOutcome.Ack)
                {
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: ct);
                }
                else
                {
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: ct);
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < MaxTransientAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Transient error handling '{RoutingKey}' (attempt {Attempt}/{Max}); retrying in {DelaySeconds}s",
                    ea.RoutingKey,
                    attempt,
                    MaxTransientAttempts,
                    RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed handling '{RoutingKey}' after {Max} attempts; rejecting message without requeue",
                    ea.RoutingKey,
                    MaxTransientAttempts);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: ct);
                return;
            }
        }
    }

    private async Task EnsureTopicExchangeExistsAsync(IConnection connection, string exchange, CancellationToken ct)
    {
        try
        {
            await using var probe = await connection.CreateChannelAsync(cancellationToken: ct);
            await probe.ExchangeDeclarePassiveAsync(exchange, cancellationToken: ct);
        }
        catch (OperationInterruptedException)
        {
            // Local/fresh broker where the topic exchange does not exist yet.
            await using var setup = await connection.CreateChannelAsync(cancellationToken: ct);
            await setup.ExchangeDeclareAsync(
                exchange: exchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: ct);
            logger.LogInformation("Created missing topic exchange '{Exchange}'", exchange);
        }
    }
}
