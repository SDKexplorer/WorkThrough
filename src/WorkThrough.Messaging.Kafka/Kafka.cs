using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

/// <summary>What a host does with Kafka: publish messages, consume topics, or both.</summary>
public sealed class KafkaOptions
{
    /// <summary>The header carrying the WorkThrough message ID on published messages, and read from consumed ones.</summary>
    public const string MessageIdHeader = "message-id";

    internal ProducerConfig? Producer { get; private set; }
    internal ConsumerConfig? Consumer { get; private set; }
    internal List<MessageRoute> Routes { get; } = [];

    /// <summary>
    /// Publishes <see cref="Messaging.PublishJob"/> messages: the destination is the topic and the key the message key. The producer is
    /// made idempotent and waits for every in-sync replica.
    /// </summary>
    public KafkaOptions Publish(ProducerConfig producer)
    {
        Producer = producer ?? throw new ArgumentNullException(nameof(producer));

        return this;
    }

    /// <summary>
    /// Stores each message from <paramref name="topic"/> as a job at <paramref name="target"/>, with the value as its input, then commits
    /// its offset. A message is identified by its <see cref="MessageIdHeader"/>, or else by topic, partition and offset.
    /// </summary>
    public KafkaOptions Consume<TInput>(ConsumerConfig consumer, string topic, JobDefinition<TInput> target)
    {
        if (Consumer is not null && Consumer.GroupId != consumer.GroupId)
        {
            throw new ArgumentException("All Kafka routes of a host share one consumer group.", nameof(consumer));
        }

        Consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        Routes.Add(MessageRoute.To(topic, target));

        return this;
    }
}

public static class KafkaRegistration
{
    public static WorkThroughBuilder UseKafka(this WorkThroughBuilder builder, Action<KafkaOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new KafkaOptions();
        configure(options);
        builder.Services.AddSingleton(options);
        if (options.Producer is not null)
        {
            builder.AddMessagePublisher<KafkaPublisher>();
        }

        if (options.Routes.Count > 0)
        {
            MessageIntake.Register(builder.Services);
            builder.Services.AddHostedService<KafkaConsumer>();
        }

        return builder;
    }
}

internal sealed class KafkaPublisher : IMessagePublisher, IDisposable
{
    private readonly IProducer<string?, byte[]> producer;

    public KafkaPublisher(KafkaOptions options)
    {
        var config = new ProducerConfig(options.Producer!) { EnableIdempotence = true, Acks = Acks.All };
        producer = new ProducerBuilder<string?, byte[]>(config).Build();
    }

    public async Task PublishAsync(PublishMessage message, string messageId, CancellationToken cancellationToken)
    {
        var headers = new Headers { { KafkaOptions.MessageIdHeader, Encoding.UTF8.GetBytes(messageId) } };
        foreach (var (name, value) in message.Headers ?? new Dictionary<string, string>())
        {
            headers.Add(name, Encoding.UTF8.GetBytes(value));
        }

        // Returns once the broker has written the message to every in-sync replica; throws otherwise.
        await producer.ProduceAsync(message.Destination, new Message<string?, byte[]>
        {
            Key = message.Key,
            Value = Encoding.UTF8.GetBytes(message.Body.GetRawText()),
            Headers = headers
        }, cancellationToken);
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }
}

/// <summary>Consumes the routed topics, storing each message as a job before committing its offset.</summary>
internal sealed class KafkaConsumer(KafkaOptions options, MessageIntake intake, ILogger<KafkaConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // Confluent's Consume blocks a thread, so the loop runs on its own.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Factory.StartNew(() => Consume(stoppingToken), stoppingToken,
        TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task Consume(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig(options.Consumer!) { EnableAutoCommit = false };
        using var consumer = new ConsumerBuilder<string?, byte[]>(config).Build();
        var routes = options.Routes.ToDictionary(route => route.Source, StringComparer.Ordinal);
        consumer.Subscribe(routes.Keys);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string?, byte[]> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException error)
                {
                    logger.LogError(error, "Kafka consumption failed");
                    await Task.Delay(RetryDelay, stoppingToken);

                    continue;
                }

                try
                {
                    var key = result.Message.Headers?.TryGetLastBytes(KafkaOptions.MessageIdHeader, out var id) == true
                        ? $"kafka:{result.Topic}:{Encoding.UTF8.GetString(id)}"
                        : $"kafka:{result.Topic}:{result.Partition.Value}:{result.Offset.Value}";
                    await intake.AcceptAsync(routes[result.Topic], result.Message.Value ?? [], key, null, stoppingToken);
                    consumer.Commit(result);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The job was not stored: read this message again rather than skip it.
                    logger.LogWarning(error, "A message from {Topic} was not stored; retrying it", result.Topic);
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }
}
