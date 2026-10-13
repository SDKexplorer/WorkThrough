using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace WorkThrough;

/// <summary>What a host does with RabbitMQ: publish messages, consume queues, or both.</summary>
public sealed class RabbitMqOptions
{
    internal bool Publishes { get; private set; }
    internal List<MessageRoute> Routes { get; } = [];

    /// <summary>Unacknowledged messages a consumer holds at once.</summary>
    public ushort Prefetch { get; set; } = 16;

    /// <summary>Publishes <see cref="Messaging.PublishJob"/> messages: the destination is the exchange and the key the routing key.</summary>
    public RabbitMqOptions Publish()
    {
        Publishes = true;

        return this;
    }

    /// <summary>
    /// Stores each message from <paramref name="queue"/> as a job at <paramref name="target"/>, with the body as its input, then
    /// acknowledges it. The message ID, when producers set one, deduplicates redeliveries. The queue must already exist.
    /// </summary>
    public RabbitMqOptions Consume<TInput>(string queue, JobDefinition<TInput> target)
    {
        Routes.Add(MessageRoute.To(queue, target));

        return this;
    }
}

public static class RabbitMqRegistration
{
    public static WorkThroughBuilder UseRabbitMq(this WorkThroughBuilder builder, ConnectionFactory factory, Action<RabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RabbitMqOptions();
        configure(options);
        builder.Services.AddSingleton(new RabbitMqConnection(factory));
        if (options.Publishes)
        {
            builder.AddMessagePublisher<RabbitMqPublisher>();
        }

        if (options.Routes.Count > 0)
        {
            MessageIntake.Register(builder.Services);
            builder.Services.AddSingleton(options);
            builder.Services.AddHostedService<RabbitMqConsumer>();
        }

        return builder;
    }
}

/// <summary>One connection per host, opened on first use.</summary>
internal sealed class RabbitMqConnection(ConnectionFactory factory) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (connection is not { IsOpen: true })
            {
                connection = await factory.CreateConnectionAsync(ct);
            }

            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        gate.Dispose();
    }
}

/// <summary>Publishes with confirms and mandatory routing: a publish succeeds only once the broker has routed and stored the message.</summary>
internal sealed class RabbitMqPublisher(RabbitMqConnection connections) : IMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IChannel? channel;

    public async Task PublishAsync(PublishMessage message, string messageId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (channel is not { IsOpen: true })
            {
                var connection = await connections.GetAsync(cancellationToken);
                channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true), cancellationToken);
            }

            var properties = new BasicProperties
            {
                MessageId = messageId,
                ContentType = "application/json",
                Persistent = true,
                Headers = message.Headers?.ToDictionary(header => header.Key, header => (object?)header.Value)
            };

            // With confirmation tracking, this returns after the broker confirms, and throws when it refuses or cannot route the message.
            await channel.BasicPublishAsync(message.Destination, message.Key ?? "", mandatory: true, properties,
                System.Text.Encoding.UTF8.GetBytes(message.Body.GetRawText()), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (channel is not null)
        {
            await channel.DisposeAsync();
        }

        gate.Dispose();
    }
}

/// <summary>Consumes each routed queue, storing every message as a job before acknowledging it.</summary>
internal sealed class RabbitMqConsumer(RabbitMqConnection connections, RabbitMqOptions options, MessageIntake intake, ILogger<RabbitMqConsumer> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "RabbitMQ consumption failed; reconnecting");
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var connection = await connections.GetAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, options.Prefetch, false, stoppingToken);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();

            return Task.CompletedTask;
        };
        foreach (var route in options.Routes)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => AcceptAsync(channel, route, delivery, stoppingToken);
            await channel.BasicConsumeAsync(route.Source, autoAck: false, consumer, stoppingToken);
        }

        await closed.Task.WaitAsync(stoppingToken);
    }

    private async Task AcceptAsync(IChannel channel, MessageRoute route, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        try
        {
            var key = delivery.BasicProperties.MessageId is { Length: > 0 } id ? $"rabbitmq:{route.Source}:{id}" : null;
            await intake.AcceptAsync(route, delivery.Body, key, delivery.BasicProperties.CorrelationId, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The job was not stored, so the broker keeps the message for another attempt.
            logger.LogWarning(error, "A message from {Queue} was not stored; returning it to the queue", route.Source);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }
}
