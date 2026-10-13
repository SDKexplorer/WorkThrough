using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

/// <summary>What a host does with Azure Service Bus: publish messages, consume queues and subscriptions, or both.</summary>
public sealed class AzureServiceBusOptions
{
    internal bool Publishes { get; private set; }
    internal List<(MessageRoute Route, string? Subscription)> Routes { get; } = [];

    /// <summary>Messages each consumer processes at once.</summary>
    public int MaxConcurrentCalls { get; set; } = 8;

    /// <summary>
    /// Publishes <see cref="Messaging.PublishJob"/> messages: the destination is the queue or topic and the key the partition key. The
    /// job ID is the message ID, so duplicate detection on the entity drops a repeated publish.
    /// </summary>
    public AzureServiceBusOptions Publish()
    {
        Publishes = true;

        return this;
    }

    /// <summary>Stores each message from <paramref name="queue"/> as a job at <paramref name="target"/>, with the body as its input, then completes it.</summary>
    public AzureServiceBusOptions Consume<TInput>(string queue, JobDefinition<TInput> target)
    {
        Routes.Add((MessageRoute.To(queue, target), null));

        return this;
    }

    /// <summary>Stores each message from a topic subscription as a job at <paramref name="target"/>, then completes it.</summary>
    public AzureServiceBusOptions Consume<TInput>(string topic, string subscription, JobDefinition<TInput> target)
    {
        Routes.Add((MessageRoute.To($"{topic}/{subscription}", target), subscription));

        return this;
    }
}

public static class AzureServiceBusRegistration
{
    public static WorkThroughBuilder UseAzureServiceBus(this WorkThroughBuilder builder, ServiceBusClient client, Action<AzureServiceBusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AzureServiceBusOptions();
        configure(options);
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(options);
        if (options.Publishes)
        {
            builder.AddMessagePublisher<AzureServiceBusPublisher>();
        }

        if (options.Routes.Count > 0)
        {
            MessageIntake.Register(builder.Services);
            builder.Services.AddHostedService<AzureServiceBusConsumer>();
        }

        return builder;
    }
}

internal sealed class AzureServiceBusPublisher(ServiceBusClient client) : IMessagePublisher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServiceBusSender> senders = new(StringComparer.Ordinal);

    public async Task PublishAsync(PublishMessage message, string messageId, CancellationToken cancellationToken)
    {
        var outgoing = new ServiceBusMessage(BinaryData.FromString(message.Body.GetRawText()))
        {
            MessageId = messageId,
            ContentType = "application/json",
            PartitionKey = message.Key
        };
        foreach (var (name, value) in message.Headers ?? new Dictionary<string, string>())
        {
            outgoing.ApplicationProperties[name] = value;
        }

        // Returns once Service Bus has accepted the message; throws otherwise.
        await senders.GetOrAdd(message.Destination, client.CreateSender).SendMessageAsync(outgoing, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in senders.Values)
        {
            await sender.DisposeAsync();
        }
    }
}

/// <summary>Processes each routed entity, storing every message as a job before completing it.</summary>
internal sealed class AzureServiceBusConsumer(ServiceBusClient client, AzureServiceBusOptions options, MessageIntake intake,
    ILogger<AzureServiceBusConsumer> logger) : IHostedService, IAsyncDisposable
{
    private readonly List<ServiceBusProcessor> processors = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (route, subscription) in options.Routes)
        {
            var settings = new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = options.MaxConcurrentCalls };
            var processor = subscription is null
                ? client.CreateProcessor(route.Source, settings)
                : client.CreateProcessor(route.Source[..route.Source.LastIndexOf('/')], subscription, settings);
            processor.ProcessMessageAsync += async received =>
            {
                var key = received.Message.MessageId is { Length: > 0 } id ? $"servicebus:{route.Source}:{id}" : null;
                await intake.AcceptAsync(route, received.Message.Body.ToMemory(), key, received.Message.CorrelationId, received.CancellationToken);
                await received.CompleteMessageAsync(received.Message, received.CancellationToken);
            };

            // A message whose job was not stored is abandoned and delivered again.
            processor.ProcessErrorAsync += failed =>
            {
                logger.LogWarning(failed.Exception, "A message from {Entity} was not stored", failed.EntityPath);

                return Task.CompletedTask;
            };
            processors.Add(processor);
            await processor.StartProcessingAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var processor in processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var processor in processors)
        {
            await processor.DisposeAsync();
        }
    }
}
