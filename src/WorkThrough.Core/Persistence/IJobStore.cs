namespace WorkThrough;

/// <summary>Atomic operations a durable provider must implement and verify.</summary>
public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Commits acceptance and any receipt; immediate envelopes use the storage clock before persistence.</summary>
    Task<Guid> EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default);
    Task SeedLimitsAsync(IReadOnlyList<ConcurrencyLimit> limits, CancellationToken cancellationToken = default);
    Task SetLimitAsync(ConcurrencyLimit limit, CancellationToken cancellationToken = default);
    Task<JobRecord?> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default);
    Task<bool> RenewAsync(Guid jobId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(Guid jobId, Guid leaseToken, JobResult result, CancellationToken cancellationToken = default);
    Task<bool> FailAsync(Guid jobId, Guid leaseToken, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default);
    Task<bool> RejectAsync(Guid jobId, Guid leaseToken, JobFailure failure, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider does not implement permanent rejection.");
    Task<bool> ReleaseAsync(Guid jobId, Guid leaseToken, string reason, CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task RecordHandlerAsync(Guid jobId, Guid leaseToken, string handler, CancellationToken cancellationToken = default);
    Task<Guid> StartWorkflowAsync(FlowDefinition definition, System.Text.Json.JsonElement input, string? correlationId, CancellationToken cancellationToken = default);
    /// <summary>Atomically persists the request receipt, run, and steps; rejects conflicting reuse of a key.</summary>
    Task<Guid> StartWorkflowAsync(FlowDefinition definition, System.Text.Json.JsonElement input, string? correlationId, string idempotencyKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider does not support idempotent flow starts.");

    /// <summary>Starts a run with optional correlation, idempotency key and admission partition for every step.</summary>
    Task<Guid> StartWorkflowAsync(FlowDefinition definition, System.Text.Json.JsonElement input, FlowStart start, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider does not support flow start options.");
}

/// <summary>Assembly-independent read models used by the dashboard and diagnostics.</summary>
public interface IJobQueries
{
    Task<JobRecord?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListAsync(JobFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobAttempt>> AttemptsAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<JobStatistics> StatisticsAsync(CancellationToken cancellationToken = default);
    Task<WorkflowRun?> GetWorkflowAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListFlowJobsAsync(Guid runId, CancellationToken cancellationToken = default);
}
