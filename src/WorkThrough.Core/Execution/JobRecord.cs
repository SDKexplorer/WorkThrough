using System.Text.Json;

namespace WorkThrough;

public enum JobStatus
{
    Waiting, Queued, Running, Succeeded, Failed, Skipped, Cancelled, Rejected, Expired, AwaitingSignal
}

/// <summary>Persisted data only. Handler types are deliberately absent.</summary>
public sealed record JobRecord(
    Guid Id,
    JobContractId Contract,
    JsonElement Payload,
    string Queue,
    string? CorrelationId,
    JobStatus Status,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset AvailableAt,
    string? IdempotencyKey = null,
    Guid? WorkflowRunId = null,
    string? StepId = null,
    JsonElement? Output = null,
    string? Error = null,
    string? WorkerId = null,
    Guid? LeaseToken = null,
    DateTimeOffset? LeaseExpiresAt = null, DateTimeOffset? ExpiresAt = null, Guid? CausationJobId = null);

public sealed record JobAttempt(
    Guid JobId, int Number, string WorkerId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string Status, string? Error, string? Handler);

public sealed record JobFilter(string? CorrelationId = null, JobStatus? Status = null, int Limit = 100, int Offset = 0);

public sealed record ClaimRequest(
    string WorkerId, IReadOnlyCollection<JobContractId> Contracts,
    IReadOnlyCollection<string> Queues, TimeSpan LeaseDuration, long RegistryRevision = 0);

/// <summary>A persisted cluster-wide admission policy. Null JobKey means all job types.</summary>
public sealed record ConcurrencyLimit(string? JobKey, int Maximum);

public sealed record JobStatistics(IReadOnlyDictionary<JobStatus, long> Counts, IReadOnlyList<ConcurrencyLimit> Limits);
