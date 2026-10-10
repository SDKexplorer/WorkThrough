using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkThrough;

/// <summary>Persisted by name. Renaming a member is a storage compatibility change.</summary>
public enum JobStatus
{
    Waiting, Queued, Running, Succeeded, Failed, Skipped, Cancelled, Rejected, Expired, AwaitingSignal
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) => status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Skipped
        or JobStatus.Cancelled or JobStatus.Rejected or JobStatus.Expired;

    /// <summary>Terminal without success. A suspending flow treats these steps as incidents.</summary>
    public static bool IsUnsuccessful(this JobStatus status) => status is JobStatus.Failed or JobStatus.Cancelled
        or JobStatus.Rejected or JobStatus.Expired;
}

/// <summary>How one attempt ended. Persisted and archived by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptStatus>))]
public enum AttemptStatus
{
    Running, Succeeded, Failed, Rejected, Deferred, Interrupted, Abandoned, Cancelled
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
    DateTimeOffset? LeaseExpiresAt = null, DateTimeOffset? ExpiresAt = null, Guid? CausationJobId = null)
{
    /// <summary>For immediate acceptance envelopes, assign creation and eligibility from the destination storage clock.</summary>
    /// <remarks>Preserved by the outbox; cleared on acceptance. Existing envelopes without this flag retain their explicit times.</remarks>
    public bool UseStorageTime { get; init; }
}

public sealed record JobAttempt(
    Guid JobId, int Number, string WorkerId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, AttemptStatus Status, string? Error, string? Handler);

public sealed record JobFilter(string? CorrelationId = null, JobStatus? Status = null, int Limit = 100, int Offset = 0);

public sealed record ClaimRequest(
    string WorkerId, IReadOnlyCollection<JobContractId> Contracts,
    IReadOnlyCollection<string> Queues, TimeSpan LeaseDuration, long RegistryRevision = 0);

/// <summary>A persisted cluster-wide admission policy. Null JobKey means all job types.</summary>
public sealed record ConcurrencyLimit(string? JobKey, int Maximum);

public sealed record JobStatistics(IReadOnlyDictionary<JobStatus, long> Counts, IReadOnlyList<ConcurrencyLimit> Limits);
