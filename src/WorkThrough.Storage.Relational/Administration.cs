using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private Task<T> AdminAsync<T>(AdministrativeCommand command, string action, string target, object arguments,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000
            || command.Actor is null || string.IsNullOrWhiteSpace(command.Actor.Issuer) || string.IsNullOrWhiteSpace(command.Actor.Subject))
            throw new ArgumentException("Administrative commands require an ID, trusted actor, and a reason of 1–1000 characters.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { command.Actor, command.Reason, action, target, arguments }, JobJson.Options))));
        return WriteAsync(async (connection, transaction, ct) =>
        {
            await using (var query = Command(connection, transaction, "SELECT fingerprint,result FROM dj_commands WHERE id=@id", ("id", command.Id.ToString())))
            await using (var reader = await query.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    if (reader.GetString(0) != fingerprint) throw new StateConflictException("Command ID was already used for a different operation or actor.");
                    return JsonSerializer.Deserialize<T>(reader.GetString(1), JobJson.Options)!;
                }
            }
            var result = await change(connection, transaction, ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_commands(id,fingerprint,result) VALUES (@id,@fingerprint,@result)", ct,
                ("id", command.Id.ToString()), ("fingerprint", fingerprint), ("result", JsonSerializer.Serialize(result, JobJson.Options)));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_audit(command_id,at,actor,action,target,reason,outcome) VALUES (@id,@at,@actor,@action,@target,@reason,'Applied')
                """, ct, ("id", command.Id.ToString()), ("at", await NowAsync(connection, transaction, ct)),
                ("actor", JsonSerializer.Serialize(command.Actor, JobJson.Options)), ("action", action), ("target", target), ("reason", command.Reason));
            var changes = action is "definitions.edit" or "schedules.edit"
                ? JsonSerializer.Serialize(new { definition = target, draftRevision = result }, JobJson.Options)
                : JsonSerializer.Serialize(new { arguments, result }, JobJson.Options);
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_audit_changes(command_id,changes) VALUES (@id,@changes)", ct,
                ("id", command.Id.ToString()), ("changes", changes));
            return result;
        }, cancellationToken);
    }

    public Task<bool> RetryJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "jobs.retry", id.ToString(), new { expectedAttempt }, async (connection, transaction, ct) =>
        {
            var job = await ExpectedJobAsync(connection, transaction, id, expectedAttempt, ct);
            if (!await RetryJobCoreAsync(connection, transaction, id, ct)) throw new StateConflictException("Only failed or cancelled standalone jobs can be retried.");
            await JournalAsync(connection, transaction, job, await NowAsync(connection, transaction, ct), "RetryRequested", ct);
            return true;
        }, cancellationToken);

    public Task<bool> CancelJobAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "jobs.cancel", id.ToString(), new { expectedAttempt }, async (connection, transaction, ct) =>
        {
            var job = await ExpectedJobAsync(connection, transaction, id, expectedAttempt, ct);
            if (!await CancelJobCoreAsync(connection, transaction, id, ct)) throw new StateConflictException("The job is already terminal.");
            if (job.Status != JobStatus.Running)
                await JournalAsync(connection, transaction, job, await NowAsync(connection, transaction, ct), "Cancelled", ct);
            return true;
        }, cancellationToken);

    public Task<bool> RecoverStepAsync(Guid id, int expectedAttempt, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "flows.recover", id.ToString(), new { expectedAttempt }, async (connection, transaction, ct) =>
        {
            var job = await ExpectedJobAsync(connection, transaction, id, expectedAttempt, ct);
            if (job.WorkflowRunId is not { } run || job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
                throw new StateConflictException("Recovery requires a failed or cancelled flow step.");
            if (await ScalarAsync(connection, transaction, "SELECT status FROM dj_flows WHERE id=@id", ct, ("id", run.ToString())) is not "Incident")
                throw new StateConflictException("The flow must have a recoverable incident.");
            var now = await NowAsync(connection, transaction, ct);
            await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET status='Queued',available_at=@now,error=NULL,max_attempts=attempt+1 WHERE id=@id", ct,
                ("now", now), ("id", id.ToString()));
            await ExecuteAsync(connection, transaction, "UPDATE dj_flows SET status='Running',error=NULL WHERE id=@id", ct, ("id", run.ToString()));
            await JournalAsync(connection, transaction, job, now, "RecoveryRequested", ct);
            await AdvanceWorkflowAsync(connection, transaction, run, ct);
            return true;
        }, cancellationToken);

    public Task<bool> SetFlowPausedAsync(Guid id, bool paused, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, paused ? "flows.pause" : "flows.resume", id.ToString(), new { paused }, async (connection, transaction, ct) =>
        {
            var expected = paused ? "Running" : "Paused";
            if (await ExecuteAsync(connection, transaction, "UPDATE dj_flows SET status=@status WHERE id=@id AND status=@expected", ct,
                ("status", paused ? "Paused" : "Running"), ("id", id.ToString()), ("expected", expected)) != 1)
                throw new StateConflictException($"Flow must be {expected}.");
            if (!paused) await AdvanceWorkflowAsync(connection, transaction, id, ct);
            return true;
        }, cancellationToken);

    public Task<bool> ChangeLimitAsync(ConcurrencyLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        Validate(limit);
        return AdminAsync(command, "limits.manage", limit.JobKey ?? "*", limit, async (connection, transaction, ct) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dj_limits(job_key,maximum) VALUES (@key,@maximum) ON CONFLICT(job_key) DO UPDATE SET maximum=excluded.maximum", ct,
                ("key", limit.JobKey ?? "*"), ("maximum", limit.Maximum));
            return true;
        }, cancellationToken);
    }

    private static async Task<JobRecord> ExpectedJobAsync(DbConnection connection, DbTransaction transaction, Guid id, int attempt, CancellationToken ct)
    {
        var job = (await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE id=@id", ct, ("id", id.ToString()))).SingleOrDefault();
        if (job is null || job.Attempt != attempt) throw new StateConflictException("The job changed. Refresh its state before acting.");
        return job;
    }

    public async Task<IReadOnlyList<AuditEntry>> AuditAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var query = Command(connection, null, "SELECT a.*,c.changes FROM dj_audit a LEFT JOIN dj_audit_changes c ON a.command_id=c.command_id ORDER BY a.at DESC,a.command_id LIMIT @limit", ("limit", limit));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AuditEntry>();
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                JsonSerializer.Deserialize<OperationActor>(reader.GetString(2), JobJson.Options)!, reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), Text(reader, "changes")));
        return entries;
    }
}
