namespace WorkThrough;

/// <summary>A trusted host supplies the issuer and stable subject; display names are historical labels.</summary>
public sealed record OperationActor(string Issuer, string Subject, string? DisplayName = null)
{
    public const string ServiceIssuer = "service";

    /// <summary>An application, script or deployment acting through code rather than a signed-in user.</summary>
    public static OperationActor Service(string name) => new(ServiceIssuer, name, name);
}

public sealed record AdministrativeCommand(Guid Id, OperationActor Actor, string Reason)
{
    /// <summary>A command with a fresh ID. To retry the same command safely after a lost response, resend this same instance.</summary>
    public static AdministrativeCommand New(OperationActor actor, string reason) => new(Guid.NewGuid(), actor, reason);
}

public sealed record AuditEntry(Guid CommandId, DateTimeOffset At, OperationActor Actor, string Action, string Target, string Reason, string Outcome, string? Changes = null);

public static class AuditOutcomes
{
    public const string Applied = "Applied";
}

/// <summary>Stored state no longer permits the request. Operator commands report this as a <see cref="ErrorType.Conflict"/> result.</summary>
public class StateConflictException(string message) : InvalidOperationException(message);

/// <summary>Operator commands commit the mutation, deduplication receipt, and audit in one transaction.</summary>
/// <remarks>Expected validation, missing-state and conflict failures are returned as results; nothing is committed for them.</remarks>
public interface IJobAdministration
{
    Task<Result> RetryJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result> CancelJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result> RecoverStepAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result> SetFlowPausedAsync(Guid id, bool paused, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result> ChangeLimitAsync(ConcurrencyLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the keyed limit for its key scope and job key.</summary>
    Task<Result> SetKeyedLimitAsync(KeyedLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default);

    /// <summary>Removes the keyed limit for a job key, or the all-work limit when the job key is null.</summary>
    Task<Result> RemoveKeyedLimitAsync(KeyScope scope, string? jobKey, AdministrativeCommand command, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the rate limit for its job key and per-key scope.</summary>
    Task<Result> SetRateLimitAsync(RateLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default);

    Task<Result> RemoveRateLimitAsync(string? jobKey, KeyScope? perKey, AdministrativeCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> AuditAsync(int limit = 100, CancellationToken cancellationToken = default);
}

public enum DefinitionStatus
{
    Draft, Published, Retired
}

public sealed record StoredFlowDefinition(FlowDefinition Definition, DefinitionStatus Status, int Revision);

/// <summary>Draft revisions use optimistic concurrency; published graph versions are immutable. Results carry the new revision.</summary>
public interface IWorkflowDefinitions
{
    Task<Result<int>> SaveDraftAsync(FlowDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> PublishAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> RetireAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<StoredFlowDefinition?> GetDefinitionAsync(string key, int version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredFlowDefinition>> DefinitionsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<FlowDefinition?> GetRunDefinitionAsync(Guid runId, CancellationToken cancellationToken = default);
}
