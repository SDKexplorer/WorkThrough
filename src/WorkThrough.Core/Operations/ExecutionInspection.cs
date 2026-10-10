namespace WorkThrough;

/// <summary>Permission-protected text for one attempt. Null failure means no recorded exception details.</summary>
public sealed record ExecutionDiagnostic(string StoreId, DateTimeOffset ObservedAt, Guid ExecutionId,
    int Attempt, bool ExecutionExists, bool AttemptExists, JobFailure? Failure, bool TextTruncated)
{
    public string Coverage => "live";
}

/// <summary>Saved run metadata, without input, definition templates, condition literals or error text.</summary>
public sealed record FlowMetadata(Guid Id, string Key, int Version, string? CorrelationId, FlowStatus Status, bool MetadataTruncated);

/// <summary>Why <see cref="FlowInspection.Dependencies"/> is or is not populated.</summary>
public static class FlowGraphStatuses
{
    public const string Available = "available";
    public const string NotLive = "not_live";
    public const string DefinitionMissing = "definition_missing";
    public const string DefinitionTooLarge = "definition_too_large";
}

/// <summary>A saved dependency addressed by durable job IDs; labels are not graph identity.</summary>
public sealed record FlowDependency(Guid PredecessorId, Guid SuccessorId);

/// <summary>Separately paged nodes, edges and retained events from one read snapshot.</summary>
/// <remarks>Offsets address immutable run structure. A missing live run can still have retained journal entries.
/// RequestedRangeTrimmed refers to the global journal floor, not proof that this run lost events.</remarks>
public sealed record FlowInspection(string StoreId, DateTimeOffset ObservedAt, FlowMetadata? Flow,
    IReadOnlyList<ExecutionMetadata> Nodes, int? NextNodeOffset, IReadOnlyList<FlowDependency> Dependencies,
    int? NextDependencyOffset, string GraphStatus, IReadOnlyList<ExecutionEvent> Events, long? NextAfterEvent,
    long RetainedAfterEvent, long LastEventSequence, bool RequestedRangeTrimmed)
{
    public string Coverage => "live-and-retained-journal";
}

/// <summary>Bounded investigative reads. Hosts must authorize resource access and sensitive diagnostic text separately.</summary>
public interface IExecutionInspectionQueries
{
    /// <summary>Reads one positive attempt number; fields allow 1–4096 Unicode characters, default 2048.</summary>
    Task<ExecutionDiagnostic> ReadDiagnosticAsync(Guid id, int attempt, int maximumCharacters = OperationQueryLimits.DefaultDiagnosticCharacters,
        CancellationToken cancellationToken = default);

    /// <summary>Reads at most 100 nodes, dependencies and events per section. Oversized saved definitions report unavailable edges explicitly.</summary>
    Task<FlowInspection> ReadFlowAsync(Guid runId, int nodeOffset = 0, int dependencyOffset = 0,
        long afterEvent = 0, int limit = OperationQueryLimits.DefaultPageSize, CancellationToken cancellationToken = default);
}
