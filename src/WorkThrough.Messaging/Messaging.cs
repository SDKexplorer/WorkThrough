using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WorkThrough;

/// <summary>A message to publish: a broker destination, an optional key, a JSON body and headers.</summary>
/// <param name="Destination">A RabbitMQ exchange, a Kafka topic or an Azure Service Bus queue or topic.</param>
/// <param name="Key">A RabbitMQ routing key, a Kafka message key or an Azure Service Bus partition key.</param>
public sealed record PublishMessage(string Destination, JsonElement Body, string? Key = null, IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>A message whose body is <paramref name="message"/> as JSON.</summary>
    public static PublishMessage Of<T>(string destination, T message, string? key = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(destination, JsonSerializer.SerializeToElement(message, JobJson.Options), key, headers);
}

/// <summary>Sends a message to a broker and returns once the broker has accepted it.</summary>
public interface IMessagePublisher
{
    /// <param name="messageId">Stable across retries of the same publish, so consumers and brokers can deduplicate.</param>
    Task PublishAsync(PublishMessage message, string messageId, CancellationToken cancellationToken);
}

public static class Messaging
{
    /// <summary>The job that publishes a message. Enqueue it, or use <see cref="Publish{T}"/>, to publish durably.</summary>
    public static JobDefinition<PublishMessage> PublishJob { get; } = new("workthrough.messaging.publish", displayName: "Publish message");

    /// <summary>
    /// Publishes <paramref name="message"/> through a job: it is accepted durably now and published, with retries, until the broker
    /// confirms. The job ID is the message ID. Await the request to commit it.
    /// </summary>
    public static EnqueueRequest<PublishMessage> Publish<T>(this JobClient jobs, string destination, T message, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs.Enqueue(PublishJob, PublishMessage.Of(destination, message, key));
    }

    /// <summary>Runs <see cref="PublishJob"/> with <typeparamref name="TPublisher"/>, registered as a singleton.</summary>
    public static WorkThroughBuilder AddMessagePublisher<TPublisher>(this WorkThroughBuilder builder) where TPublisher : class, IMessagePublisher
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IMessagePublisher, TPublisher>();

        return builder.Job(PublishJob, job => job.Run(async call =>
        {
            await call.Service<IMessagePublisher>().PublishAsync(call.Input, call.Context.JobId.ToString("N"), call.CancellationToken);
        }));
    }
}

/// <summary>Where a consumed message goes: the job address, queue and attempt budget its job gets.</summary>
public sealed record MessageRoute(string Source, JobContractId Target, string Queue, int MaxAttempts)
{
    public static MessageRoute To<TInput>(string source, JobDefinition<TInput> target) => new(source, target.Identity, target.Queue, target.MaxAttempts);
}

/// <summary>
/// Stores a consumed message as a job whose input is the message's JSON body. A body that is not JSON is stored as a JSON string, so its job
/// is rejected visibly instead of the message being lost. Acknowledge the message only after this returns.
/// </summary>
public sealed class MessageIntake(JobClient jobs)
{
    /// <param name="idempotencyKey">Identifies the message across redeliveries; null when the broker gives no stable identity.</param>
    public Task<Guid> AcceptAsync(MessageRoute route, ReadOnlyMemory<byte> body, string? idempotencyKey, string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);

        return jobs.EnqueueAsync(route.Target, Body(body),
            new(Queue: route.Queue, MaxAttempts: route.MaxAttempts, IdempotencyKey: idempotencyKey, CorrelationId: correlationId), cancellationToken);
    }

    private static JsonElement Body(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(body.Span));
        }
    }

    /// <summary>Registers the intake; broker packages call this.</summary>
    public static void Register(IServiceCollection services) => services.TryAddSingleton<MessageIntake>();
}
