namespace WorkThrough;

/// <summary>Shared limits count admitted, unexpired leases, not external side effects or local handler tasks.</summary>
public sealed record CapacityObservation(int? GlobalMaximum, long GlobalAdmitted, int? ProcessorMaximum, long ProcessorAdmitted);

/// <summary>A recent heartbeat observation; matching requires the processor/version and queue on the same worker.</summary>
public sealed record ReadinessWorker(string WorkerId, DateTimeOffset SeenAt, int Capacity, int Running, bool Draining,
    bool Matches, bool DetailsAvailable, bool MetadataTruncated, int? AcquisitionFailures);

/// <summary>Why work is or is not progressing. Terminal executions report their <see cref="JobStatus"/> name instead.</summary>
/// <remarks>Reasons stay text so older readers tolerate reasons introduced by newer writers.</remarks>
public static class ReadinessReasons
{
    public const string NotLive = "NotLive";
    public const string Running = "Running";
    public const string LeaseExpired = "LeaseExpired";
    public const string ExecutionDeadlineExceeded = "ExecutionDeadlineExceeded";
    public const string ProgressOverdue = "ProgressOverdue";
    public const string FlowIncident = "FlowIncident";
    public const string FlowPaused = "FlowPaused";
    public const string AwaitingSignal = "AwaitingSignal";
    public const string Dependencies = "Dependencies";
    public const string PendingExpiry = "PendingExpiry";
    public const string QueuePaused = "QueuePaused";
    public const string RetryScheduled = "RetryScheduled";
    public const string Scheduled = "Scheduled";
    public const string SharedCapacity = "SharedCapacity";
    public const string WorkerCoverageIncomplete = "WorkerCoverageIncomplete";
    public const string NoRecentCapableWorker = "NoRecentCapableWorker";
    public const string WorkersDraining = "WorkersDraining";
    public const string AcquisitionFailing = "AcquisitionFailing";
    public const string WorkerCapacity = "WorkerCapacity";
    public const string AwaitingAcquisition = "AwaitingAcquisition";

    public static bool IsSuspectedStall(string reason) => reason is ProgressOverdue or ExecutionDeadlineExceeded;
}

/// <summary>Metadata-only explanation at one database snapshot. Missing coverage is never evidence of an absent worker.</summary>
public sealed record ExecutionReadiness(string StoreId, DateTimeOffset ObservedAt, Guid ExecutionId, bool ExecutionExists,
    string Reason, bool SuspectedStalled, FlowStatus? FlowStatus, bool QueuePaused, DateTimeOffset? AvailableAt,
    DateTimeOffset? LeaseExpiresAt, DateTimeOffset? Deadline, DateTimeOffset? LastProgressAt,
    CapacityObservation? Capacity, IReadOnlyList<ReadinessWorker> WorkerExamples, int ObservedMatchingWorkers,
    bool WorkerExamplesTruncated, bool WorkerCoverageIncomplete)
{
    public const int DefaultWorkerExamples = 10;

    public string Coverage => "live";
    public int HeartbeatFreshnessSeconds => (int)WorkerHeartbeat.Freshness.TotalSeconds;
}

/// <summary>Attempt completion window, half-open, default one hour, maximum seven days. Counts exclude archived attempts.</summary>
public sealed record RuntimeMetricsQuery(DateTimeOffset? From = null, DateTimeOffset? Until = null);

/// <summary>Windowed attempt metrics; a successful attempt is not a count of distinct jobs or external effects.</summary>
public sealed record RuntimeMetrics(string StoreId, DateTimeOffset ObservedAt, DateTimeOffset From, DateTimeOffset Until,
    long FinishedAttempts, long SucceededAttempts, long FailedAttempts, double? MeanDurationMilliseconds,
    long? MaximumDurationMilliseconds)
{
    public string Coverage => "live-attempts";
    public double SuccessfulAttemptsPerSecond => SucceededAttempts / (Until - From).TotalSeconds;
}

/// <summary>Bounded runtime evidence without payload, handler, progress description or lease-token reads.</summary>
public interface IRuntimeQueries
{
    Task<ExecutionReadiness> ReadReadinessAsync(Guid id, int workerExamples = ExecutionReadiness.DefaultWorkerExamples, CancellationToken cancellationToken = default);
    Task<RuntimeMetrics> ReadRuntimeMetricsAsync(RuntimeMetricsQuery query, CancellationToken cancellationToken = default);
}
