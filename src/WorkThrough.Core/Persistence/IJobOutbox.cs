namespace WorkThrough;

/// <summary>Status is one of <see cref="OutboxStatuses"/>.</summary>
public sealed record OutboxDelivery(Guid Id, string Status, Guid? JobId);

public static class OutboxStatuses
{
    public const string Pending = "Pending";
    public const string Delivering = "Delivering";
    public const string Delivered = "Delivered";
}

/// <summary>Transfers accepted outbox envelopes with stable destination deduplication keys.</summary>
public interface IJobOutbox
{
    Task<int> DispatchOutboxAsync(IJobStore destination, int batchSize = 20, CancellationToken cancellationToken = default);
    Task<OutboxDelivery?> OutboxDeliveryAsync(Guid id, CancellationToken cancellationToken = default);
}
