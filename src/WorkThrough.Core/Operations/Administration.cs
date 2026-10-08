namespace WorkThrough;

/// <summary>A trusted host supplies the issuer and stable subject; display names are historical labels.</summary>
public sealed record OperationActor(string Issuer, string Subject, string? DisplayName = null);
public sealed record AdministrativeCommand(Guid Id, OperationActor Actor, string Reason);
public sealed record AuditEntry(Guid CommandId, DateTimeOffset At, OperationActor Actor, string Action, string Target, string Reason, string Outcome, string? Changes = null);
public sealed class StateConflictException(string message) : InvalidOperationException(message);

/// <summary>Operator commands commit the mutation, deduplication receipt, and audit in one transaction.</summary>
public interface IJobAdministration
{
    Task<bool> RetryJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<bool> CancelJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<bool> RecoverStepAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<bool> SetFlowPausedAsync(Guid id, bool paused, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<bool> ChangeLimitAsync(ConcurrencyLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> AuditAsync(int limit = 100, CancellationToken cancellationToken = default);
}

public enum DefinitionStatus
{
    Draft, Published, Retired
}
public sealed record StoredFlowDefinition(FlowDefinition Definition, DefinitionStatus Status, int Revision);

/// <summary>Draft revisions use optimistic concurrency; published graph versions are immutable.</summary>
public interface IWorkflowDefinitions
{
    Task<int> SaveDraftAsync(FlowDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<int> PublishAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<int> RetireAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<StoredFlowDefinition?> GetDefinitionAsync(string key, int version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredFlowDefinition>> DefinitionsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<FlowDefinition?> GetRunDefinitionAsync(Guid runId, CancellationToken cancellationToken = default);
}
