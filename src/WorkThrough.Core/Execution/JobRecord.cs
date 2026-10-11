using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkThrough;

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

    /// <summary>Optional admission partition, such as a customer ID. Independent of <see cref="CorrelationId"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartitionKey { get; init; }

    /// <summary>Optional owning tenant for tenant-scoped admission. Independent of <see cref="PartitionKey"/>; not an access boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }
}

public sealed record JobAttempt(
    Guid JobId, int Number, string WorkerId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, AttemptStatus Status, string? Error, string? Handler);

public sealed record JobFilter(string? CorrelationId = null, JobStatus? Status = null, int Limit = 100, int Offset = 0)
{
    /// <summary>Matches any of these tenants; empty matches nothing. Tenant-scoped hosts set it from <see cref="TenantAccess"/>.</summary>
    public IReadOnlyList<string>? TenantIds { get; init; }
}

public sealed record ClaimRequest(
    string WorkerId, IReadOnlyCollection<JobContractId> Contracts,
    IReadOnlyCollection<string> Queues, TimeSpan LeaseDuration, long RegistryRevision = 0)
{
    public const int MaximumBatch = 100;

    /// <summary>How many jobs one claim may lease; a worker asks for its free slots.</summary>
    public int MaximumJobs { get; init; } = 1;

    /// <summary>The worker's remaining local capacity per job-key group, applied as the batch fills.</summary>
    public IReadOnlyList<ContractBudget> Budgets { get; init; } = [];
}

/// <summary>At most <paramref name="Remaining"/> more leases for any of these job keys in one claim.</summary>
public sealed record ContractBudget(IReadOnlyCollection<string> JobKeys, int Remaining);

/// <summary>A persisted cluster-wide admission policy. Null JobKey means all job types.</summary>
public sealed record ConcurrencyLimit(string? JobKey, int Maximum);

public sealed record JobStatistics(IReadOnlyDictionary<JobStatus, long> Counts, IReadOnlyList<ConcurrencyLimit> Limits);
