namespace WorkThrough;

public sealed record EventPosition(long LastSequence, long RetainedAfter);
public interface IExecutionEvents
{
    Task<EventPosition> EventPositionAsync(CancellationToken cancellationToken = default);
    Task<long> SubscribeAsync(string subscriber, long startAfter = 0, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionEvent>> ReadEventsAsync(long after, int limit = 100, CancellationToken cancellationToken = default);
    Task<bool> AcknowledgeAsync(string subscriber, long expectedSequence, long sequence, CancellationToken cancellationToken = default);
    Task<int> TrimEventsAsync(long through, AdministrativeCommand command, CancellationToken cancellationToken = default);
}

/// <summary>At-least-once subscription callback; deduplicate external effects using subscriber name and event sequence.</summary>
public interface IExecutionEventHandler
{
    Task HandleAsync(ExecutionEvent entry, CancellationToken cancellationToken);
}
