namespace WorkThrough;

public sealed record QueueControl(string Queue, bool Paused, int Revision)
{
    public const int DefaultWeight = 1;
    public const int MaximumWeight = 1000;

    /// <summary>Relative share of claims when several queues have ready work; 1 to <see cref="MaximumWeight"/>.</summary>
    public int Weight { get; init; } = DefaultWeight;
}

public sealed record WorkerControlState(string WorkerId, bool Draining, int Revision);

/// <summary>Control changes return the new revision.</summary>
public interface IExecutionOperations
{
    Task<IReadOnlyList<QueueControl>> QueuesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerControlState>> WorkerControlsAsync(CancellationToken cancellationToken = default);
    Task<Result<int>> SetQueuePausedAsync(string queue, bool paused, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    /// <summary>Sets a queue's fair-share weight. The value is the new control revision.</summary>
    Task<Result<int>> SetQueueWeightAsync(string queue, int weight, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> SetWorkerDrainingAsync(string workerId, bool draining, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);

    /// <summary>Creates new work with a new effect key and a causation link; never rewrites the original.</summary>
    Task<Result<Guid>> ReplayAsync(Guid jobId, AdministrativeCommand command, CancellationToken cancellationToken = default);
}
