namespace WorkThrough;

/// <summary>Sensitive diagnostic data; hosts can redact it before persistence.</summary>
public sealed record JobFailure(string ExceptionType, string Message, string? StackTrace)
{
    public static JobFailure FromException(Exception exception) => new(exception.GetType().FullName ?? exception.GetType().Name,
        exception.Message, exception.ToString());
}
public sealed record AttemptFailure(int Attempt, JobFailure Failure);
public sealed record JobProgress(DateTimeOffset StartedAt, DateTimeOffset LastProgressAt, DateTimeOffset? Deadline,
    TimeSpan? ProgressTimeout, string? Description);
public sealed record JobHealth(string Reason, bool SuspectedStalled, DateTimeOffset ObservedAt, JobProgress? Progress);
public sealed record ExecutionEvent(long Sequence, Guid JobId, Guid? RunId, DateTimeOffset At, string Kind, int Attempt);
public sealed record ProcessorDescription(JobContractId Identity, JobContractId CanonicalIdentity, string? DisplayName);
public sealed record WorkerHeartbeat(string WorkerId, DateTimeOffset SeenAt, IReadOnlyList<JobContractId> Processors,
    IReadOnlyList<string> Queues, int Capacity, int Running, IReadOnlyList<ProcessorDescription>? Descriptions = null)
{
    public WorkerAcquisition? Acquisition { get; init; }
}
/// <summary>Worker-reported acquisition observations. A successful empty poll also resets consecutive failures.</summary>
public sealed record WorkerAcquisition(DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSucceededAt, int ConsecutiveFailures);

public interface IJobDiagnostics
{
    Task<bool> FailWithDetailsAsync(Guid id, Guid leaseToken, JobFailure failure, TimeSpan retryDelay, CancellationToken cancellationToken = default);
    Task<bool> BeginAttemptAsync(Guid id, Guid leaseToken, TimeSpan? executionTimeout, TimeSpan? progressTimeout, CancellationToken cancellationToken = default);
    Task<bool> ReportProgressAsync(Guid id, Guid leaseToken, string? description, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttemptFailure>> FailuresAsync(Guid id, CancellationToken cancellationToken = default);
    Task<JobHealth?> HealthAsync(Guid id, CancellationToken cancellationToken = default);
    Task HeartbeatAsync(WorkerHeartbeat worker, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerHeartbeat>> WorkersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionEvent>> EventsAsync(Guid runId, long after = 0, CancellationToken cancellationToken = default);
}
