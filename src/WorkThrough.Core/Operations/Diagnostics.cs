namespace WorkThrough;

/// <summary>Sensitive diagnostic data; hosts can redact it before persistence.</summary>
public sealed record JobFailure(string ExceptionType, string Message, string? StackTrace)
{
    public static JobFailure FromException(Exception exception) => new(exception.GetType().FullName ?? exception.GetType().Name,
        exception.Message, exception.ToString());

    /// <summary>The single-line form stored as the job and attempt error.</summary>
    public string ToSummary() => $"{ExceptionType}: {Message}";
}

public sealed record AttemptFailure(int Attempt, JobFailure Failure);

public sealed record JobProgress(DateTimeOffset StartedAt, DateTimeOffset LastProgressAt, DateTimeOffset? Deadline,
    TimeSpan? ProgressTimeout, string? Description);

/// <summary>Reason is one of <see cref="ReadinessReasons"/> or the name of a terminal <see cref="JobStatus"/>.</summary>
public sealed record JobHealth(string Reason, bool SuspectedStalled, DateTimeOffset ObservedAt, JobProgress? Progress);

/// <summary>Kind is one of <see cref="ExecutionEventKinds"/>, an <see cref="AttemptStatus"/> name, or the <see cref="JobStatus"/> a flow step entered.</summary>
/// <remarks>Kind stays text so older readers tolerate kinds introduced by newer writers.</remarks>
public sealed record ExecutionEvent(long Sequence, Guid JobId, Guid? RunId, DateTimeOffset At, string Kind, int Attempt);

/// <summary>Journal kinds that are not an attempt outcome or a flow step state.</summary>
public static class ExecutionEventKinds
{
    public const string Started = "Started";
    public const string RetryRequested = "RetryRequested";
    public const string RecoveryRequested = "RecoveryRequested";
    public const string Replayed = "Replayed";
    public const string ScheduledOccurrence = "ScheduledOccurrence";
    public const string SignalReceived = "SignalReceived";
    public const string SignalExpired = "SignalExpired";
    public const string ChildFlowSucceeded = "ChildFlowSucceeded";
    public const string ChildFlowFailed = "ChildFlowFailed";
    public const string ChildFlowStarted = "ChildFlowStarted";
}

public sealed record ProcessorDescription(JobContractId Identity, JobContractId CanonicalIdentity, string? DisplayName)
{
    /// <summary>For an upcast address, the version whose job runs its stored work.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobContractId? UpcastTo { get; init; }
}

public sealed record WorkerHeartbeat(string WorkerId, DateTimeOffset SeenAt, IReadOnlyList<JobContractId> Processors,
    IReadOnlyList<string> Queues, int Capacity, int Running, IReadOnlyList<ProcessorDescription>? Descriptions = null)
{
    /// <summary>A heartbeat older than this, in database time, is not evidence of a running worker.</summary>
    public static TimeSpan Freshness { get; } = TimeSpan.FromSeconds(60);

    public WorkerAcquisition? Acquisition { get; init; }

    /// <summary>The highest <see cref="EngineProtocol"/> level the worker understands. Heartbeats from releases before protocols report the baseline.</summary>
    public int Protocol { get; init; } = EngineProtocol.Baseline;
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
