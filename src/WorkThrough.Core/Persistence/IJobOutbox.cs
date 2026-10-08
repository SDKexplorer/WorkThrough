namespace WorkThrough;

public sealed record OutboxDelivery(Guid Id, string Status, Guid? JobId);

/// <summary>Transfers accepted outbox envelopes with stable destination deduplication keys.</summary>
public interface IJobOutbox
{
    Task<int> DispatchOutboxAsync(IJobStore destination, int batchSize = 20, CancellationToken cancellationToken = default);
    Task<OutboxDelivery?> OutboxDeliveryAsync(Guid id, CancellationToken cancellationToken = default);
}
