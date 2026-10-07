using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Shared data mapping; concrete providers own connection and admission locking semantics.</summary>
public abstract partial class RelationalJobStore : IJobStore, IJobQueries, IJobAdministration, IWorkflowDefinitions, IJobDiagnostics, IJobSchedules
{
    protected abstract DbConnection CreateConnection();
    protected abstract string ClockSql { get; }
    protected abstract Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(async (connection, transaction, ct) =>
        {
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS dj_schema (version INTEGER NOT NULL PRIMARY KEY)", ct);
            var version = await ScalarAsync(connection, transaction, "SELECT MAX(version) FROM dj_schema", ct);
            if (version is not null and not DBNull && Convert.ToInt32(version) is not (1 or 2))
                throw new InvalidOperationException("Unsupported WorkThrough schema version. Use a compatible package and migration.");
            await ExecuteAsync(connection, transaction, Schema, ct);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_definition_lifecycle(flow_key,version,definition,status,revision)
                SELECT flow_key,version,definition,'Published',1 FROM dj_definitions WHERE 1=1 ON CONFLICT DO NOTHING;
                INSERT INTO dj_schema(version) VALUES (2) ON CONFLICT DO NOTHING;
                """, ct);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Guid> EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        if (job.IdempotencyKey is not null)
        {
            var existing = await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE idempotency_key=@key", ct, ("key", job.IdempotencyKey));
            if (existing.Count > 0)
            {
                var saved = existing[0];
                if (saved.Contract != job.Contract || saved.Payload.GetRawText() != job.Payload.GetRawText()
                    || saved.Queue != job.Queue || saved.CorrelationId != job.CorrelationId)
                    throw new InvalidOperationException("Idempotency key is already associated with different work.");
                return saved.Id;
            }
        }
        await InsertJobAsync(connection, transaction, job, ct);
        return job.Id;
    }, cancellationToken);

    public Task SeedLimitsAsync(IReadOnlyList<ConcurrencyLimit> limits, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        foreach (var limit in limits)
        {
            Validate(limit);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dj_limits(job_key, maximum) VALUES (@key,@maximum) ON CONFLICT DO NOTHING", ct,
                ("key", limit.JobKey ?? "*"), ("maximum", limit.Maximum));
        }
        return true;
    }, cancellationToken);

    public Task SetLimitAsync(ConcurrencyLimit limit, CancellationToken cancellationToken = default)
    {
        Validate(limit);
        return WriteAsync(async (connection, transaction, ct) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dj_limits(job_key,maximum) VALUES (@key,@maximum) ON CONFLICT(job_key) DO UPDATE SET maximum=excluded.maximum", ct,
                ("key", limit.JobKey ?? "*"), ("maximum", limit.Maximum));
            return true;
        }, cancellationToken);
    }

    public Task<JobRecord?> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("Claim requires a worker ID and positive lease duration.");
        if (request.Contracts.Count == 0 || request.Queues.Count == 0) return Task.FromResult<JobRecord?>(null);
        return WriteAsync<JobRecord?>(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            await RecoverExpiredAsync(connection, transaction, now, ct);
            var active = Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now", ct, ("now", now)));
            var maximum = await ScalarAsync(connection, transaction, "SELECT maximum FROM dj_limits WHERE job_key='*'", ct);
            if (maximum is not null and not DBNull && active >= Convert.ToInt64(maximum)) return null;

            var parameters = new List<(string, object?)> { ("now", now) };
            var contracts = request.Contracts.Select((contract, index) =>
            {
                parameters.Add(($"key{index}", contract.Key));
                parameters.Add(($"version{index}", contract.Version));
                return $"(j.job_key=@key{index} AND j.contract_version=@version{index})";
            }).ToArray();
            var queues = request.Queues.Select((queue, index) =>
            {
                parameters.Add(($"queue{index}", queue));
                return $"@queue{index}";
            }).ToArray();
            var sql = $"""
                SELECT j.* FROM dj_jobs j
                WHERE j.status='Queued' AND j.available_at<=@now
                  AND (j.workflow_run_id IS NULL OR EXISTS (
                    SELECT 1 FROM dj_flows f WHERE f.id=j.workflow_run_id AND f.status='Running'))
                  AND ({string.Join(" OR ", contracts)}) AND j.queue IN ({string.Join(",", queues)})
                  AND NOT EXISTS (
                    SELECT 1 FROM dj_limits l WHERE l.job_key=j.job_key AND l.maximum <= (
                      SELECT COUNT(*) FROM dj_jobs active WHERE active.job_key=j.job_key
                        AND active.status='Running' AND active.lease_expires_at>@now))
                ORDER BY j.available_at,j.created_at,j.id LIMIT 1
                """;
            var candidates = await ReadJobsAsync(connection, transaction, sql, ct, parameters.ToArray());
            if (candidates.Count == 0) return null;
            var candidate = candidates[0];
            var token = Guid.NewGuid();
            var expires = checked(now + (long)request.LeaseDuration.TotalMilliseconds);
            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status='Running',attempt=attempt+1,worker_id=@worker,
                lease_token=@token,lease_expires_at=@expires,error=NULL WHERE id=@id
                """, ct, ("id", candidate.Id.ToString()), ("worker", request.WorkerId), ("token", token.ToString()), ("expires", expires));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_attempts(job_id,number,worker_id,started_at,status)
                VALUES (@id,@number,@worker,@now,'Running')
                """, ct, ("id", candidate.Id.ToString()), ("number", candidate.Attempt + 1), ("worker", request.WorkerId), ("now", now));
            await JournalAsync(connection, transaction, candidate with { Attempt = candidate.Attempt + 1 }, now, "Started", ct);
            return candidate with { Status = JobStatus.Running, Attempt = candidate.Attempt + 1,
                WorkerId = request.WorkerId, LeaseToken = token, LeaseExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires) };
        }, cancellationToken);
    }

    public Task<bool> RenewAsync(Guid jobId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            return await ExecuteAsync(connection, transaction,
                "UPDATE dj_jobs SET lease_expires_at=@expires WHERE id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", ct,
                ("expires", checked(now + (long)duration.TotalMilliseconds)), ("id", jobId.ToString()), ("token", leaseToken.ToString()), ("now", now)) == 1;
        }, cancellationToken);
    }

    public Task<bool> CompleteAsync(Guid jobId, Guid leaseToken, JobResult result, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, result, null, TimeSpan.Zero, false, cancellationToken);

    public Task<bool> FailAsync(Guid jobId, Guid leaseToken, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, null, error, retryDelay, false, cancellationToken);

    public Task<bool> ReleaseAsync(Guid jobId, Guid leaseToken, string reason, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, null, reason, TimeSpan.Zero, true, cancellationToken);

    private Task<bool> SettleAsync(Guid jobId, Guid token, JobResult? result, string? error, TimeSpan delay, bool interrupted, CancellationToken cancellationToken, JobFailure? failure = null) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            var jobs = await ReadJobsAsync(connection, transaction,
                "SELECT * FROM dj_jobs WHERE id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", ct,
                ("id", jobId.ToString()), ("token", token.ToString()), ("now", now));
            if (jobs.Count == 0) return false;
            var job = jobs[0];
            var status = result is not null ? JobStatus.Succeeded : interrupted || job.Attempt < job.MaxAttempts ? JobStatus.Queued : JobStatus.Failed;
            if (status == JobStatus.Queued && !await WorkflowAllowsRetryAsync(connection, transaction, job, ct))
                status = JobStatus.Skipped;
            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status=@status,output=@output,error=@error,available_at=@available,max_attempts=max_attempts+@interrupted,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ct, ("status", status.ToString()), ("output", result?.Output.GetRawText()), ("error", Truncate(error)),
                ("available", checked(now + Math.Max(0, (long)delay.TotalMilliseconds))), ("interrupted", interrupted ? 1 : 0), ("id", jobId.ToString()));
            await FinishAttemptAsync(connection, transaction, job, now,
                result is not null ? "Succeeded" : interrupted ? "Interrupted" : "Failed", error, ct);
            if (failure is not null)
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO dj_failures(job_id,attempt,exception_type,message,stack_trace)
                    VALUES (@id,@attempt,@type,@message,@stack)
                    """, ct, ("id", job.Id.ToString()), ("attempt", job.Attempt), ("type", Clip(failure.ExceptionType, 500)),
                    ("message", Clip(failure.Message, 2000)), ("stack", Clip(failure.StackTrace, 32000)));
            if (job.WorkflowRunId is { } flowId) await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
            return true;
        }, cancellationToken);

    public Task<bool> RetryAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, ct) => RetryJobCoreAsync(connection, transaction, jobId, ct), cancellationToken);

    private async Task<bool> RetryJobCoreAsync(DbConnection connection, DbTransaction transaction, Guid jobId, CancellationToken ct)
    {
        var now = await NowAsync(connection, transaction, ct);
        // Keep the attempt history and grant one more attempt. Flow migration/replay is a separate operation.
        return await ExecuteAsync(connection, transaction, """
            UPDATE dj_jobs SET status='Queued',available_at=@now,error=NULL,max_attempts=attempt+1
            WHERE id=@id AND status IN ('Failed','Cancelled') AND workflow_run_id IS NULL
            """, ct, ("now", now), ("id", jobId.ToString())) == 1;
    }

    public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, ct) => CancelJobCoreAsync(connection, transaction, jobId, ct), cancellationToken);

    private async Task<bool> CancelJobCoreAsync(DbConnection connection, DbTransaction transaction, Guid jobId, CancellationToken ct)
    {
        var jobs = await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE id=@id", ct, ("id", jobId.ToString()));
        if (jobs.Count == 0 || jobs[0].Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Skipped or JobStatus.Cancelled) return false;
        var job = jobs[0];
        var now = await NowAsync(connection, transaction, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE dj_jobs SET status='Cancelled',error='Cancellation requested',lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id", ct, ("id", jobId.ToString()));
        if (job.Status == JobStatus.Running) await FinishAttemptAsync(connection, transaction, job, now, "Cancelled", "Cancellation requested", ct);
        if (job.WorkflowRunId is { } flowId) await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
        return true;
    }

    public Task RecordHandlerAsync(Guid jobId, Guid leaseToken, string handler, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE dj_attempts SET handler=@handler WHERE job_id=@id AND status='Running'
            AND number=(SELECT attempt FROM dj_jobs WHERE id=@id AND lease_token=@token)
            """, ct, ("handler", handler), ("id", jobId.ToString()), ("token", leaseToken.ToString()));
        return true;
    }, cancellationToken);

    private async Task RecoverExpiredAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct)
    {
        var expired = await ReadJobsAsync(connection, transaction,
            "SELECT * FROM dj_jobs WHERE status='Running' AND lease_expires_at<=@now ORDER BY lease_expires_at LIMIT 100", ct, ("now", now));
        foreach (var job in expired)
        {
            var status = job.Attempt < job.MaxAttempts ? "Queued" : "Failed";
            if (status == "Queued" && !await WorkflowAllowsRetryAsync(connection, transaction, job, ct))
                status = "Skipped";
            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status=@status,error='Worker lease expired',available_at=@now,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ct, ("status", status), ("now", now), ("id", job.Id.ToString()));
            await FinishAttemptAsync(connection, transaction, job, now, "Abandoned", "Worker lease expired", ct);
            if (job.WorkflowRunId is { } flowId && status == "Failed") await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
        }
    }

    private static async Task<bool> WorkflowAllowsRetryAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct) =>
        job.WorkflowRunId is not { } runId || await ScalarAsync(connection, transaction,
            "SELECT status FROM dj_flows WHERE id=@id", ct, ("id", runId.ToString())) is "Running" or "Paused" or "Incident";

    private static async Task FinishAttemptAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, string status, string? error, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            "UPDATE dj_attempts SET finished_at=@now,status=@status,error=@error WHERE job_id=@id AND number=@number AND status='Running'", ct,
            ("now", now), ("status", status), ("error", Truncate(error)), ("id", job.Id.ToString()), ("number", job.Attempt));
        await JournalAsync(connection, transaction, job, now, status, ct);
    }

    private static Task<int> JournalAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, string kind, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO dj_events(sequence,job_id,run_id,at,kind,attempt)
            SELECT COALESCE(MAX(sequence),0)+1,@id,@run,@now,@kind,@attempt FROM dj_events
            """, ct, ("id", job.Id.ToString()), ("run", job.WorkflowRunId?.ToString()), ("now", now), ("kind", kind), ("attempt", job.Attempt));

    private static string? Clip(string? value, int maximum) => value is not null && value.Length > maximum ? value[..maximum] : value;

    protected async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = CreateConnection();
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<T> WriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await BeginWriteAsync(connection, ct).ConfigureAwait(false);
        var result = await action(connection, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private async Task<long> NowAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction, ClockSql, ct).ConfigureAwait(false));

    protected static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    protected static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static string? Truncate(string? error) => error is { Length: > 2000 } ? error[..2000] : error;
    private static void Validate(ConcurrencyLimit limit)
    {
        if (limit.Maximum < 0) throw new ArgumentOutOfRangeException(nameof(limit), "A limit cannot be negative. Zero pauses admission.");
        if (limit.JobKey is not null) _ = new JobContractId(limit.JobKey, 1);
    }
}
