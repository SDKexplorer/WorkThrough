using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using WorkThrough.Testing;
using Xunit;

namespace WorkThrough.Tests;

public sealed record BrokerGreeting(string Name);

/// <summary>Publishing through jobs and consuming brokers into jobs, on real RabbitMQ and Kafka.</summary>
public sealed class MessagingTests
{
    private static readonly JobDefinition<BrokerGreeting> Greeted = new("tests.messaging.greeted");

    public sealed class Received
    {
        private readonly List<string> names = [];

        public void Add(string name)
        {
            lock (names)
            {
                names.Add(name);
            }
        }

        public string[] Names
        {
            get
            {
                lock (names)
                {
                    return [.. names];
                }
            }
        }
    }

    private static Action<WorkThroughBuilder> Record(Action<WorkThroughBuilder> broker) => workthrough =>
    {
        workthrough.Job(Greeted, job => job.Run(call =>
        {
            call.Service<Received>().Add(call.Input.Name);

            return Task.CompletedTask;
        }));
        broker(workthrough);
    };

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WorkThroughTestHost.DefaultTimeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    [RabbitMqFact]
    public async Task RabbitMq_messages_published_through_jobs_arrive_as_jobs_once_and_unroutable_ones_fail_visibly()
    {
        var factory = new ConnectionFactory { Uri = new(Environment.GetEnvironmentVariable("WORKTHROUGH_RABBITMQ")!) };
        var queue = "wt-test-" + Guid.NewGuid().ToString("N");
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: true);

        await using var host = await WorkThroughTestHost.StartAsync(Record(workthrough => workthrough.UseRabbitMq(factory, rabbit => rabbit.Publish().Consume(queue, Greeted))),
            services => services.AddSingleton<Received>());
        var received = host.Services.GetRequiredService<Received>();

        await host.Jobs.Publish("", new BrokerGreeting("ada"), key: queue);
        var duplicate = new BasicProperties { MessageId = "fixed-id" };
        for (var copy = 0; copy < 2; copy++)
        {
            await channel.BasicPublishAsync("", queue, mandatory: false, duplicate, Encoding.UTF8.GetBytes("""{"name":"grace"}"""));
        }

        await UntilAsync(() => received.Names.Length >= 2);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(["ada", "grace"], received.Names.Order());

        var unroutable = await host.Jobs.Publish("", new BrokerGreeting("nobody"), key: "no-such-queue-" + Guid.NewGuid().ToString("N"));
        await UntilAsync(() => host.Queries.GetAsync(unroutable).Result is { Attempt: > 0, Status: not JobStatus.Running });
        Assert.NotEqual(JobStatus.Succeeded, (await host.Queries.GetAsync(unroutable))!.Status);
    }

    [KafkaFact]
    public async Task Kafka_messages_published_through_jobs_arrive_as_jobs_once()
    {
        var servers = Environment.GetEnvironmentVariable("WORKTHROUGH_KAFKA")!;
        var topic = "wt-test-" + Guid.NewGuid().ToString("N");
        var consumer = new ConsumerConfig { BootstrapServers = servers, GroupId = topic, AutoOffsetReset = AutoOffsetReset.Earliest };
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = servers }).Build())
        {
            await admin.CreateTopicsAsync([new() { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        }

        await using var host = await WorkThroughTestHost.StartAsync(Record(workthrough => workthrough.UseKafka(kafka => kafka
            .Publish(new ProducerConfig { BootstrapServers = servers })
            .Consume(consumer, topic, Greeted))), services => services.AddSingleton<Received>());
        var received = host.Services.GetRequiredService<Received>();

        await host.Jobs.Publish(topic, new BrokerGreeting("ada"), key: "customer-1");
        using (var producer = new ProducerBuilder<string?, byte[]>(new ProducerConfig { BootstrapServers = servers }).Build())
        {
            for (var copy = 0; copy < 2; copy++)
            {
                await producer.ProduceAsync(topic, new Message<string?, byte[]>
                {
                    Value = Encoding.UTF8.GetBytes("""{"name":"grace"}"""),
                    Headers = new Confluent.Kafka.Headers { { KafkaOptions.MessageIdHeader, Encoding.UTF8.GetBytes("fixed-id") } }
                });
            }
        }

        await UntilAsync(() => received.Names.Length >= 2);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(["ada", "grace"], received.Names.Order());
    }

    [ServiceBusFact]
    public async Task Service_Bus_messages_published_through_jobs_arrive_as_jobs_once()
    {
        var queue = Environment.GetEnvironmentVariable("WORKTHROUGH_SERVICEBUS_QUEUE")!;
        await using var client = new Azure.Messaging.ServiceBus.ServiceBusClient(Environment.GetEnvironmentVariable("WORKTHROUGH_SERVICEBUS"));
        var run = Guid.NewGuid().ToString("N");

        await using var host = await WorkThroughTestHost.StartAsync(Record(workthrough => workthrough.UseAzureServiceBus(client, bus => bus.Publish().Consume(queue, Greeted))),
            services => services.AddSingleton<Received>());
        var received = host.Services.GetRequiredService<Received>();

        await host.Jobs.Publish(queue, new BrokerGreeting($"ada-{run}"));
        await using (var sender = client.CreateSender(queue))
        {
            for (var copy = 0; copy < 2; copy++)
            {
                await sender.SendMessageAsync(new($"{{\"name\":\"grace-{run}\"}}") { MessageId = $"fixed-{run}" });
            }
        }

        await UntilAsync(() => received.Names.Count(name => name.EndsWith(run, StringComparison.Ordinal)) >= 2);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal([$"ada-{run}", $"grace-{run}"], received.Names.Where(name => name.EndsWith(run, StringComparison.Ordinal)).Order());
    }
}

internal sealed class ServiceBusFactAttribute : FactAttribute
{
    public ServiceBusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKTHROUGH_SERVICEBUS")))
        {
            Skip = "Supply WORKTHROUGH_SERVICEBUS and WORKTHROUGH_SERVICEBUS_QUEUE for a disposable namespace or the emulator.";
        }
    }
}

internal sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKTHROUGH_RABBITMQ")))
        {
            Skip = "Supply WORKTHROUGH_RABBITMQ, an AMQP URI of a disposable broker.";
        }
    }
}

internal sealed class KafkaFactAttribute : FactAttribute
{
    public KafkaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKTHROUGH_KAFKA")))
        {
            Skip = "Supply WORKTHROUGH_KAFKA, the bootstrap servers of a disposable broker.";
        }
    }
}
