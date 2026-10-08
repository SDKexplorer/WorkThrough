using System.Text.Json;

namespace WorkThrough;

/// <summary>Failed/cancelled/rejected work is retained unless a separate failure cutoff is supplied.</summary>
public sealed record ArchivePolicy(DateTimeOffset SucceededBefore, DateTimeOffset? FailedBefore = null, int BatchSize = 10);
public sealed record ArchiveCandidate(Guid Id, string Kind, string? CorrelationId, DateTimeOffset CompletedAt, int Jobs);
public sealed record ArchiveSummary(Guid Id, string Kind, string? CorrelationId, DateTimeOffset ArchivedAt, int Jobs, string Sha256, bool BodyAvailable = true);
public sealed record ArchivedJob(JobRecord Job, IReadOnlyList<JobAttempt> Attempts, IReadOnlyList<AttemptFailure> Failures);
public sealed record ArchivedSignal(string Name, JsonElement Payload, DateTimeOffset ReceivedAt);
public sealed record ExecutionArchive(int FormatVersion, Guid Id, string Kind, string? CorrelationId, DateTimeOffset ArchivedAt,
    IReadOnlyList<ArchivedJob> Jobs, WorkflowRun? Flow, FlowDefinition? Definition, JsonElement? FlowInput, IReadOnlyList<ExecutionEvent> Events, IReadOnlyList<ArchivedSignal> Signals);
public sealed record ArchiveExport(ArchiveSummary Summary, string Json);

public interface IExecutionArchives
{
    Task<IReadOnlyList<ArchiveCandidate>> ArchiveCandidatesAsync(ArchivePolicy policy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ArchiveCompletedAsync(ArchivePolicy policy, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchiveSummary>> ArchivesAsync(string? correlationId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<ArchiveExport?> ExportArchiveAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Deletes only the archive body after the caller verifies an external copy; tombstone and deduplication receipts remain.</summary>
    Task<bool> PurgeArchiveAsync(Guid id, string expectedSha256, AdministrativeCommand command, CancellationToken cancellationToken = default);
}
