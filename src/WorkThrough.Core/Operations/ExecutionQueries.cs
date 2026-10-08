namespace WorkThrough;

/// <summary>The timestamp used to select executions; failure time is independent of current job state.</summary>
public enum ExecutionTimeBasis
{
    Created, FailedAttempt
}

/// <summary>Bounded live-history search. The default window is the last 24 hours; windows cannot exceed seven days.</summary>
/// <remarks>From is inclusive and Until is exclusive. Continue with the same filters and returned cursor.
/// Pages use separate read snapshots; concurrent state changes may change membership. A cursor is not authorization.</remarks>
public sealed record ExecutionQuery(
    string? CorrelationId = null,
    JobStatus? Status = null,
    string? ProcessorKey = null,
    int? ContractVersion = null,
    string? Queue = null,
    Guid? WorkflowRunId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? Until = null,
    ExecutionTimeBasis TimeBasis = ExecutionTimeBasis.Created,
    int Limit = 25,
    string? Cursor = null);

/// <summary>Metadata only: no payload, output, exception, implementation name, lease token or idempotency key.</summary>
/// <remarks>Queue, correlation and step text are bounded to 512 characters; MetadataTruncated identifies clipping.</remarks>
public sealed record ExecutionMetadata(
    Guid Id, JobContractId Processor, string Queue, string? CorrelationId,
    JobStatus Status, int Attempt, int MaxAttempts, DateTimeOffset CreatedAt,
    DateTimeOffset AvailableAt, DateTimeOffset? TerminalAt, DateTimeOffset? ExpiresAt,
    Guid? WorkflowRunId, string? StepId, Guid? CausationJobId, bool MetadataTruncated);

/// <summary>A page of live execution metadata. It deliberately does not claim archive coverage or an exact total.</summary>
public sealed record ExecutionPage(
    string StoreId, DateTimeOffset ObservedAt, DateTimeOffset From, DateTimeOffset Until,
    ExecutionTimeBasis TimeBasis, IReadOnlyList<ExecutionMetadata> Items, string? NextCursor)
{
    public string Coverage => "live";
}

/// <summary>A live execution lookup; null means absent from active tables, not necessarily absent from archives.</summary>
public sealed record ExecutionLookup(string StoreId, DateTimeOffset ObservedAt, ExecutionMetadata? Execution)
{
    public string Coverage => "live";
}

/// <summary>Attempt outcome/timing without exception text or CLR implementation details.</summary>
public sealed record ExecutionAttemptMetadata(int Number, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Bounded attempt history, ordered by attempt number. The next position is exclusive.</summary>
public sealed record ExecutionAttemptPage(
    string StoreId, DateTimeOffset ObservedAt, bool ExecutionExists,
    IReadOnlyList<ExecutionAttemptMetadata> Items, int? NextAfterAttempt)
{
    public string Coverage => "live";
}

/// <summary>Read-only, assembly-independent operation queries. Hosts must enforce authorization before calling.</summary>
public interface IExecutionQueries
{
    /// <summary>Searches metadata using keyset pagination, bounded time windows and explicit live-history coverage.</summary>
    Task<ExecutionPage> SearchExecutionsAsync(ExecutionQuery query, CancellationToken cancellationToken = default);
    /// <summary>Reads metadata by ID without a time-window restriction or loading input/output.</summary>
    Task<ExecutionLookup> FindExecutionAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Reads at most 100 attempt summaries, excluding sensitive diagnostics.</summary>
    Task<ExecutionAttemptPage> ReadAttemptsAsync(Guid id, int afterAttempt = 0, int limit = 25, CancellationToken cancellationToken = default);
}
