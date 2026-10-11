using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Stored state required by a command is absent. Reported as <see cref="ErrorType.NotFound"/>.</summary>
internal sealed class MissingStateException(string message) : Exception(message);

public abstract partial class RelationalJobStore
{
    private const int MaximumCommandReasonLength = 1000;
    private const int MaximumAuditPage = 500;

    // Draft edits record the revision rather than repeating the saved definition in the audit changes.
    private static readonly HashSet<string> DraftActions = [AuditActions.EditDefinition, AuditActions.EditSchedule];

    /// <summary>Runs an audited command and reports expected failures as results. Nothing is committed for a failure.</summary>
    private async Task<Result<T>> AdminAsync<T>(AdministrativeCommand command, string action, string target, object arguments,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> change, CancellationToken ct, Action? validate = null)
    {
        try
        {
            validate?.Invoke();

            return Result.Success(await AuditedAsync(command, action, target, arguments, change, ct));
        }
        catch (ArgumentException error)
        {
            return Result.Failure<T>(Error.Validation(error.Message));
        }
        catch (MissingStateException error)
        {
            return Result.Failure<T>(Error.NotFound(error.Message));
        }
        catch (StateConflictException error)
        {
            return Result.Failure<T>(Error.Conflict(error.Message));
        }
    }

    private async Task<Result> AdminAsync(AdministrativeCommand command, string action, string target, object arguments,
        Func<DbConnection, DbTransaction, CancellationToken, Task> change, CancellationToken ct, Action? validate = null)
    {
        // Receipts store true for value-less commands, as earlier releases did.
        var result = await AdminAsync(command, action, target, arguments, async (connection, transaction, token) =>
        {
            await change(connection, transaction, token);

            return true;
        }, ct, validate);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    /// <summary>Commits the change, its deduplication receipt and audit entry together. A repeated command replays its receipt.</summary>
    private Task<T> AuditedAsync<T>(AdministrativeCommand command, string action, string target, object arguments,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> change, CancellationToken cancellationToken)
    {
        ValidateCommand(command);
        var fingerprint = Fingerprint(new
        {
            command.Actor,
            command.Reason,
            action,
            target,
            arguments
        });

        return WriteAsync(async (connection, transaction, ct) =>
        {
            await using (var query = Command(connection, transaction, "SELECT fingerprint,result FROM dj_commands WHERE id=@id", ("id", command.Id.ToString())))
            {
                await using var reader = await query.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    return reader.GetString(0) == fingerprint
                        ? JsonSerializer.Deserialize<T>(reader.GetString(1), JobJson.Options)!
                        : throw new StateConflictException("Command ID was already used for a different operation or actor.");
                }
            }

            var result = await change(connection, transaction, ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_commands(id,fingerprint,result) VALUES (@id,@fingerprint,@result)", ct,
                ("id", command.Id.ToString()), ("fingerprint", fingerprint), ("result", JsonSerializer.Serialize(result, JobJson.Options)));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_audit(command_id,at,actor,action,target,reason,outcome) VALUES (@id,@at,@actor,@action,@target,@reason,@outcome)
                """, ct, ("id", command.Id.ToString()), ("at", await NowAsync(connection, transaction, ct)),
                ("actor", JsonSerializer.Serialize(command.Actor, JobJson.Options)), ("action", action), ("target", target),
                ("reason", command.Reason), ("outcome", AuditOutcomes.Applied));
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_audit_changes(command_id,changes) VALUES (@id,@changes)", ct,
                ("id", command.Id.ToString()), ("changes", AuditChanges(action, target, arguments, result)));

            return result;
        }, cancellationToken);
    }

    private static void ValidateCommand(AdministrativeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > MaximumCommandReasonLength
            || command.Actor is null || string.IsNullOrWhiteSpace(command.Actor.Issuer) || string.IsNullOrWhiteSpace(command.Actor.Subject))
        {
            throw new ArgumentException($"Administrative commands require an ID, trusted actor, and a reason of 1–{MaximumCommandReasonLength} characters.");
        }
    }

    private static string AuditChanges<T>(string action, string target, object arguments, T result) => DraftActions.Contains(action)
        ? JsonSerializer.Serialize(new { definition = target, draftRevision = result }, JobJson.Options)
        : JsonSerializer.Serialize(new { arguments, result }, JobJson.Options);

    public Task<Result> RetryJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.RetryJob, id.ToString(), new { expectedAttempt }, async (connection, transaction, ct) =>
        {
            var job = await ExpectedJobAsync(connection, transaction, id, expectedAttempt, ct);
            if (!await RetryJobCoreAsync(connection, transaction, id, ct))
            {
                throw new StateConflictException("Only failed or cancelled standalone jobs can be retried.");
            }

            await JournalAsync(connection, transaction, job, await NowAsync(connection, transaction, ct), ExecutionEventKinds.RetryRequested, ct);
        }, cancellationToken);

    public Task<Result> CancelJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.CancelJob, id.ToString(), new { expectedAttempt }, async (connection, transaction, ct) =>
        {
            await ExpectedJobAsync(connection, transaction, id, expectedAttempt, ct);
            if (!await CancelJobCoreAsync(connection, transaction, id, ct))
            {
                throw new StateConflictException("The job is already terminal.");
            }
        }, cancellationToken);

    public Task<Result> RecoverStepAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.RecoverStep, id.ToString(), new { expectedAttempt }, async (connection, transaction, ct) =>
        {
            await LockFlowsOfAsync(connection, transaction, [id], ct);
            var job = await ExpectedJobAsync(connection, transaction, id, expectedAttempt, ct);
            if (job.WorkflowRunId is not { } run || !job.Status.IsUnsuccessful())
            {
                throw new StateConflictException("Recovery requires a failed or cancelled flow step.");
            }

            if (await ReadFlowStatusAsync(connection, transaction, run, ct) != FlowStatus.Incident)
            {
                throw new StateConflictException("The flow must have a recoverable incident.");
            }

            var now = await NowAsync(connection, transaction, ct);
            var resumed = job.Contract == SignalWait.Processor ? JobStatus.Waiting : JobStatus.Queued;
            await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET status=@status,available_at=@now,error=NULL,max_attempts=attempt+1 WHERE id=@id", ct,
                ("status", resumed.ToString()), ("now", now), ("id", id.ToString()));
            await SetFlowStatusAsync(connection, transaction, run, FlowStatus.Running, ct);
            await JournalAsync(connection, transaction, job, now, ExecutionEventKinds.RecoveryRequested, ct);
            await AdvanceWorkflowAsync(connection, transaction, run, ct);
        }, cancellationToken);

    public Task<Result> SetFlowPausedAsync(Guid id, bool paused, AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        var (expected, next) = paused ? (FlowStatus.Running, FlowStatus.Paused) : (FlowStatus.Paused, FlowStatus.Running);

        return AdminAsync(command, paused ? AuditActions.PauseFlow : AuditActions.ResumeFlow, id.ToString(), new { paused }, async (connection, transaction, ct) =>
        {
            if (await ExecuteAsync(connection, transaction, "UPDATE dj_flows SET status=@status WHERE id=@id AND status=@expected", ct,
                ("status", next.ToString()), ("id", id.ToString()), ("expected", expected.ToString())) != 1)
            {
                throw new StateConflictException($"Flow must be {expected}.");
            }

            if (!paused)
            {
                await AdvanceWorkflowAsync(connection, transaction, id, ct);
            }
        }, cancellationToken);
    }

    public Task<Result> ChangeLimitAsync(ConcurrencyLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.ChangeLimit, limit.JobKey ?? AllJobsLimitKey, limit, async (connection, transaction, ct) =>
        {
            await SaveLimitAsync(connection, transaction, limit, replace: true, ct);
        }, cancellationToken, validate: () => Validate(limit));

    private async Task<JobRecord> ExpectedJobAsync(DbConnection connection, DbTransaction transaction, Guid id, int attempt, CancellationToken ct)
    {
        var job = await ReadJobAsync(connection, transaction, id, ct) ?? throw new MissingStateException("The job does not exist in the active store.");

        return job.Attempt == attempt ? job : throw new StateConflictException("The job changed. Refresh its state before acting.");
    }

    private Task<int> SetFlowStatusAsync(DbConnection connection, DbTransaction transaction, Guid runId, FlowStatus status, CancellationToken ct, string? error = null) =>
        ExecuteAsync(connection, transaction, "UPDATE dj_flows SET status=@status,error=@error WHERE id=@id", ct,
            ("status", status.ToString()), ("error", error), ("id", runId.ToString()));

    public Task<IReadOnlyList<AuditEntry>> AuditAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumAuditPage)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ReadAsync<IReadOnlyList<AuditEntry>>(async (connection, transaction, ct) =>
        {
            await using var query = Command(connection, transaction, $"""
                SELECT a.command_id,a.at,a.actor,a.action,a.target,a.reason,a.outcome,c.changes
                FROM dj_audit a LEFT JOIN dj_audit_changes c ON a.command_id=c.command_id
                ORDER BY a.at DESC,a.command_id {PageSql("@limit")}
                """, ("limit", limit));
            await using var reader = await query.ExecuteReaderAsync(ct);
            var entries = new List<AuditEntry>();
            while (await reader.ReadAsync(ct))
            {
                var actor = JsonSerializer.Deserialize<OperationActor>(reader.GetString(2), JobJson.Options)!;
                entries.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), actor,
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), Text(reader, "changes")));
            }

            return entries;
        }, cancellationToken);
    }
}
