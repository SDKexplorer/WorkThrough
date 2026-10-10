namespace WorkThrough;

/// <summary>Durations are measured using database time. Cleanup is disabled until explicitly enabled.</summary>
public sealed record RetentionPolicy(TimeSpan SucceededAfter, TimeSpan? FailedAfter = null, bool Enabled = false,
    int BatchSize = 1, TimeSpan? PurgeAfter = null, string? ExportDestination = null, TimeSpan? EventsAfter = null);

public sealed record StoredRetentionPolicy(RetentionPolicy Policy, int Revision);

public sealed record RetentionHold(Guid UnitId, bool Held, int Revision);

/// <summary>Code is one of <see cref="RetentionIncidentCodes"/>.</summary>
public sealed record RetentionIncident(Guid UnitId, string Code, DateTimeOffset At);

public static class RetentionIncidentCodes
{
    public const string ArchiveEvidenceLimit = "ArchiveEvidenceLimit";
    public const string ArchiveExportVerificationFailed = "ArchiveExportVerificationFailed";
}

/// <summary>Outcome is one of <see cref="MaintenanceOutcomes"/>.</summary>
public sealed record MaintenanceResult(Guid BatchId, string Outcome, DateTimeOffset CompletedAt,
    IReadOnlyList<Guid> Archived, IReadOnlyList<Guid> Purged, IReadOnlyList<Guid> Blocked, int EventsTrimmed);

public static class MaintenanceOutcomes
{
    public const string Completed = "Completed";
    public const string PolicySuperseded = "PolicySuperseded";
}

/// <summary>Values reported by <see cref="RetentionStatus.LastFailureCode"/>.</summary>
public static class MaintenanceFailureCodes
{
    public const string BatchBudgetExceeded = "BatchBudgetExceeded";
    public const string BatchFailed = "BatchFailed";
}
/// <summary>Metadata only; no lease secrets, archive bodies, hold reasons or actor labels.</summary>
public sealed record RetentionStatus(string StoreId, DateTimeOffset ObservedAt, StoredRetentionPolicy? Configuration,
    Guid? PendingBatchId, bool MaintenanceActive, DateTimeOffset? LeaseExpiresAt, MaintenanceResult? LastBatch,
    long HeldUnits, long IncidentCount, IReadOnlyList<RetentionIncident> IncidentExamples,
    long RetainedAfterEvent, long LastEventSequence, long? SlowestSubscriberPosition,
    string? LastFailureCode, DateTimeOffset? LastFailureAt)
{
    public string Coverage => "live-maintenance-and-retained-journal";
}

public interface IRetentionQueries
{
    Task<RetentionStatus> ReadRetentionStatusAsync(CancellationToken cancellationToken = default);
    Task<RetentionHold?> FindRetentionHoldAsync(Guid unitId, CancellationToken cancellationToken = default);
}

public interface IRetentionAdministration
{
    /// <summary>The value is the new policy revision.</summary>
    Task<Result<int>> ConfigureRetentionAsync(RetentionPolicy policy, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);

    /// <summary>A live step ID is resolved to its whole flow. Use the returned unit ID for later changes.</summary>
    Task<Result<RetentionHold>> SetRetentionHoldAsync(Guid unitId, bool held, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);

    /// <summary>The value is false when the unit had no recorded incident.</summary>
    Task<Result<bool>> RetryRetentionIncidentAsync(Guid unitId, AdministrativeCommand command, CancellationToken cancellationToken = default);
}

/// <summary>A trusted maintenance host owns this secret; never expose it through operations metadata.</summary>
public sealed record MaintenanceLease(Guid BatchId, Guid Token, DateTimeOffset ExpiresAt, string StoreId,
    string? ExportDestination, DateTimeOffset? PurgeBefore, int BatchSize);

/// <summary>Evidence from a trusted export destination after reading back and hashing its immutable object.</summary>
public sealed record VerifiedArchiveExport(Guid ArchiveId, string Sha256, string Destination, string ObjectVersion, long Bytes);

/// <summary>Maintenance mutations are fenced by an unexpired lease and retain durable command receipts.</summary>
public interface IRetentionMaintenance
{
    Task<MaintenanceLease?> AcquireMaintenanceAsync(string owner, OperationActor actor, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchiveSummary>> ReadExportCandidatesAsync(MaintenanceLease lease, CancellationToken cancellationToken = default);
    Task RecordVerifiedExportAsync(MaintenanceLease lease, VerifiedArchiveExport export, CancellationToken cancellationToken = default);
    Task RecordExportIncidentAsync(MaintenanceLease lease, Guid archiveId, CancellationToken cancellationToken = default);
    Task RecordMaintenanceFailureAsync(MaintenanceLease lease, bool budgetExceeded, CancellationToken cancellationToken = default);
    Task<MaintenanceResult> RunMaintenanceBatchAsync(MaintenanceLease lease, CancellationToken cancellationToken = default);
}

/// <summary>Host-configured destination; paths and credentials are never supplied by jobs or MCP clients.</summary>
public interface IArchiveDestination
{
    string Id { get; }
    Task<VerifiedArchiveExport> StoreAndVerifyAsync(string storeId, ArchiveExport archive, CancellationToken cancellationToken = default);
}
