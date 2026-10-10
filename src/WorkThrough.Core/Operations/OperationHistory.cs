namespace WorkThrough;

/// <summary>Audit metadata filters. Windows are half-open, default to 24 hours and cannot exceed seven days.</summary>
public sealed record AuditQuery(string? Action = null, string? Target = null, DateTimeOffset? From = null,
    DateTimeOffset? Until = null, int Limit = 25, string? Cursor = null);

/// <summary>Attributed operation without reason, display name, arguments, result or change contents.</summary>
public sealed record AuditMetadata(Guid CommandId, DateTimeOffset At, string? Issuer, string? Subject,
    string Action, string Target, string Outcome, bool ActorAvailable, bool MetadataTruncated);

/// <summary>A separately observed page of retained administrative evidence; no exact total is implied.</summary>
public sealed record AuditPage(string StoreId, DateTimeOffset ObservedAt, DateTimeOffset From,
    DateTimeOffset Until, IReadOnlyList<AuditMetadata> Items, string? NextCursor)
{
    public string Coverage => "retained-audit";
}

/// <summary>Archive summary filters. Time refers to archival, not execution or completion.</summary>
public sealed record ArchiveQuery(string? CorrelationId = null, string? Kind = null, DateTimeOffset? From = null,
    DateTimeOffset? Until = null, int Limit = 25, string? Cursor = null);

/// <summary>Archive location, without loading the body. Flow archives are addressed by run ID, not individual step IDs.</summary>
public sealed record ArchiveMetadata(Guid Id, string Kind, string? CorrelationId, DateTimeOffset ArchivedAt,
    int Jobs, bool BodyAvailable, bool MetadataTruncated);

/// <summary>Paged archive summaries, including summaries whose bodies have been purged.</summary>
public sealed record ArchivePage(string StoreId, DateTimeOffset ObservedAt, DateTimeOffset From,
    DateTimeOffset Until, IReadOnlyList<ArchiveMetadata> Items, string? NextCursor)
{
    public string Coverage => "archive-summary";
}

/// <summary>Point lookup without a time restriction. Null does not prove that a child step was never archived.</summary>
public sealed record ArchiveLookup(string StoreId, DateTimeOffset ObservedAt, ArchiveMetadata? Archive)
{
    public string Coverage => "archive-summary";
}

/// <summary>A whole terminal unit selected by a supplied cutoff, without reading its evidence or payload.</summary>
public sealed record RetentionCandidate(Guid Id, string Kind, string? CorrelationId, DateTimeOffset CompletedAt,
    int Jobs, bool MetadataTruncated);

/// <summary>Advisory preview of the manual archive policy; never authorizes a mutation or guarantees that evidence fits.</summary>
public sealed record RetentionPreview(string StoreId, DateTimeOffset ObservedAt, ArchivePolicy SubmittedPolicy,
    IReadOnlyList<RetentionCandidate> Candidates, bool HasMoreCandidates)
{
    public string Coverage => "live-terminal-units";
    public string PolicySource => "caller-supplied";
    public bool EvidenceSizeVerified => false;
    public IReadOnlyList<string> ExcludedCategories => ["active-work", "paused-flows", "incident-flows",
        "individual-flow-steps", "flows-over-500-jobs", .. SubmittedPolicy.FailedBefore is null ? new[] { "unspecified-failure-cutoff" } : []];
}

/// <summary>Bounded metadata reads for operational history. Hosts authorize store access and audit/retention separately.</summary>
public interface IOperationHistoryQueries
{
    Task<AuditPage> SearchAuditAsync(AuditQuery query, CancellationToken cancellationToken = default);
    Task<ArchivePage> SearchArchivesAsync(ArchiveQuery query, CancellationToken cancellationToken = default);
    Task<ArchiveLookup> FindArchiveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RetentionPreview> PreviewRetentionAsync(ArchivePolicy policy, CancellationToken cancellationToken = default);
}
