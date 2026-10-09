using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Shared data mapping; concrete providers own connection and admission locking semantics.</summary>
public abstract partial class RelationalJobStore : IJobStore, IJobQueries, IJobAdministration, IWorkflowDefinitions, IJobDiagnostics, IJobSchedules, IDeploymentQueries, IProcessorRegistry
{
    protected abstract DbConnection CreateConnection();
    protected abstract string ClockSql { get; }
    protected abstract Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken);

    public virtual async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(async (connection, transaction, ct) =>
        {
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS dj_schema (version INTEGER NOT NULL PRIMARY KEY)", ct);
            var version = await ScalarAsync(connection, transaction, "SELECT MAX(version) FROM dj_schema", ct);
            if (version is not null and not DBNull && Convert.ToInt32(version) is not (1 or 2 or 3 or 4))
            {
                throw new InvalidOperationException("Unsupported WorkThrough schema version. Use a compatible package and migration.");
            }

            await ExecuteAsync(connection, transaction, Schema, ct);
            if (version is not null and not DBNull && Convert.ToInt32(version) < 3)
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE dj_jobs ADD COLUMN expires_at BIGINT; ALTER TABLE dj_jobs ADD COLUMN causation_job_id TEXT; ALTER TABLE dj_jobs ADD COLUMN terminal_at BIGINT", ct);
                await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET terminal_at=COALESCE((SELECT MAX(finished_at) FROM dj_attempts WHERE job_id=dj_jobs.id),created_at) WHERE status IN ('Succeeded','Failed','Skipped','Cancelled')", ct);
            }

            // Old schema upgrades must add terminal_at before this optional inspection index is created.
            await ExecuteAsync(connection, transaction, "CREATE INDEX IF NOT EXISTS ix_dj_jobs_terminal ON dj_jobs(status,terminal_at,id)", ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_event_sequence(id,value) SELECT 1,COALESCE(MAX(sequence),0) FROM dj_events WHERE 1=1 ON CONFLICT DO NOTHING", ct);
            if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_store", ct)) == 0)
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_store(id) VALUES (@id)", ct, ("id", Guid.NewGuid().ToString("N")));
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_definition_lifecycle(flow_key,version,definition,status,revision)
                SELECT flow_key,version,definition,'Published',1 FROM dj_definitions WHERE 1=1 ON CONFLICT DO NOTHING;
                INSERT INTO dj_schema(version) VALUES (4) ON CONFLICT DO NOTHING;
                """, ct);

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Guid> EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, ct) => AcceptJobAsync(connection, transaction, job, ct), cancellationToken);

    private static string JobFingerprint(JobRecord job) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new
        {
            job.Contract,
            job.Payload,
            job.Queue,
            job.CorrelationId,
            job.ExpiresAt,
            job.CausationJobId
        }, JobJson.Options))));

    private static async Task<Guid> AcceptJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(job.Payload.GetRawText()) > 1_048_576)
        {
            throw new ArgumentException("Inline input exceeds 1 MiB.");
        }

        var fingerprint = JobFingerprint(job);
        if (job.IdempotencyKey is not null)
        {
            await using (var command = Command(connection, transaction, "SELECT fingerprint,job_id FROM dj_job_receipts WHERE idempotency_key=@key", ("key", job.IdempotencyKey)))
            {
                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        if (reader.GetString(0) != fingerprint)
                        {
                            throw new InvalidOperationException("Idempotency key is already associated with different work.");
                        }

                        return Guid.Parse(reader.GetString(1));
                    }
                }
            }

            var existing = await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE idempotency_key=@key", ct, ("key", job.IdempotencyKey));
            if (existing.Count > 0)
            {
                if (JobFingerprint(existing[0]) != fingerprint)
                {
                    throw new InvalidOperationException("Idempotency key is already associated with different work.");
                }

                await SaveJobReceiptAsync(connection, transaction, existing[0], ct);

                return existing[0].Id;
            }
        }

        await InsertJobAsync(connection, transaction, job, ct);
        await SaveJobReceiptAsync(connection, transaction, job, ct);

        return job.Id;
    }

    private static Task<int> SaveJobReceiptAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct) => job.IdempotencyKey is null
        ? Task.FromResult(0)
        : ExecuteAsync(connection, transaction, "INSERT INTO dj_job_receipts(idempotency_key,fingerprint,job_id) VALUES (@key,@hash,@id) ON CONFLICT DO NOTHING", ct,
            ("key", job.IdempotencyKey), ("hash", JobFingerprint(job)), ("id", job.Id.ToString()));

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
        {
            throw new ArgumentException("Claim requires a worker ID and positive lease duration.");
        }

        if (request.Contracts.Count == 0 || request.Queues.Count == 0)
        {
            return Task.FromResult<JobRecord?>(null);
        }

        return WriteAsync<JobRecord?>(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            var registry = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT revision FROM dj_registry WHERE id=1", ct));
            if (registry != request.RegistryRevision)
            {
                throw new StateConflictException("Processor registry changed. Restart this worker with the current mappings before claiming work.");
            }

            await RecoverExpiredAsync(connection, transaction, now, ct);
            await ExpireQueuedAsync(connection, transaction, now, ct);
            await DispatchSignalsCoreAsync(connection, transaction, now, 100, ct);
            if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_worker_controls WHERE worker_id=@worker AND draining=1", ct, ("worker", request.WorkerId))) > 0)
            {
                return null;
            }

            var active = Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now", ct, ("now", now)));
            var maximum = await ScalarAsync(connection, transaction, "SELECT maximum FROM dj_limits WHERE job_key='*'", ct);
            if (maximum is not null and not DBNull && active >= Convert.ToInt64(maximum))
            {
                return null;
            }

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
                WITH active_counts AS (
                  SELECT COALESCE(g.group_key,a.job_key) AS group_key,COUNT(*) AS total
                  FROM dj_jobs a LEFT JOIN dj_processor_groups g ON g.job_key=a.job_key
                  WHERE a.status='Running' AND a.lease_expires_at>@now
                  GROUP BY COALESCE(g.group_key,a.job_key)
                ), blocked_groups AS (
                  SELECT COALESCE(g.group_key,l.job_key) AS group_key
                  FROM dj_limits l LEFT JOIN dj_processor_groups g ON g.job_key=l.job_key
                  LEFT JOIN active_counts a ON a.group_key=COALESCE(g.group_key,l.job_key)
                  WHERE l.job_key<>'*' AND l.maximum<=COALESCE(a.total,0)
                )
                SELECT j.* FROM dj_jobs j
                WHERE j.status='Queued' AND j.available_at<=@now AND (j.expires_at IS NULL OR j.expires_at>@now)
                  AND NOT EXISTS (SELECT 1 FROM dj_queue_controls q WHERE q.queue=j.queue AND q.paused=1)
                  AND (j.workflow_run_id IS NULL OR EXISTS (
                    SELECT 1 FROM dj_flows f WHERE f.id=j.workflow_run_id AND f.status='Running'))
                  AND ({string.Join(" OR ", contracts)}) AND j.queue IN ({string.Join(",", queues)})
                  AND COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=j.job_key),j.job_key)
                    NOT IN (SELECT group_key FROM blocked_groups)
                ORDER BY j.available_at,j.created_at,j.id LIMIT 1
                """;
            var candidates = await ReadJobsAsync(connection, transaction, sql, ct, parameters.ToArray());
            if (candidates.Count == 0)
            {
                return null;
            }

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
            await JournalAsync(connection, transaction, candidate with
            {
                Attempt = candidate.Attempt + 1
            }, now, "Started", ct);

            return candidate with
            {
                Status = JobStatus.Running,
                Attempt = candidate.Attempt + 1,
                WorkerId = request.WorkerId,
                LeaseToken = token,
                LeaseExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires)
            };
        }, cancellationToken);
    }

    public Task<bool> RenewAsync(Guid jobId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

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

    public Task<bool> RejectAsync(Guid jobId, Guid leaseToken, JobFailure failure, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, null, $"{failure.ExceptionType}: {failure.Message}", TimeSpan.Zero, false, cancellationToken, failure, true);

    public Task<bool> FailAsync(Guid jobId, Guid leaseToken, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, null, error, retryDelay, false, cancellationToken);

    public Task<bool> ReleaseAsync(Guid jobId, Guid leaseToken, string reason, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, null, reason, TimeSpan.Zero, true, cancellationToken);

    private Task<bool> SettleAsync(Guid jobId, Guid token, JobResult? result, string? error, TimeSpan delay, bool interrupted, CancellationToken cancellationToken, JobFailure? failure = null, bool reject = false) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            var jobs = await ReadJobsAsync(connection, transaction,
                "SELECT * FROM dj_jobs WHERE id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", ct,
                ("id", jobId.ToString()), ("token", token.ToString()), ("now", now));
            if (jobs.Count == 0)
            {
                return false;
            }

            var job = jobs[0];
            result?.Validate();
            var deferred = result?.Outcome == JobOutcome.Deferred;
            reject |= result?.Outcome == JobOutcome.Rejected;
            if (deferred)
            {
                delay = result!.RetryAfter!.Value;
            }

            error ??= result?.Reason;
            var status = reject ? JobStatus.Rejected : deferred ? JobStatus.Queued : result is not null ? JobStatus.Succeeded : interrupted || job.Attempt < job.MaxAttempts ? JobStatus.Queued : JobStatus.Failed;
            if (status == JobStatus.Queued && !await WorkflowAllowsRetryAsync(connection, transaction, job, ct))
            {
                status = JobStatus.Skipped;
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status=@status,output=@output,error=@error,available_at=@available,max_attempts=max_attempts+@interrupted,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ct, ("status", status.ToString()), ("output", status == JobStatus.Succeeded ? result?.Output.GetRawText() : null), ("error", Truncate(error)),
                ("available", checked(now + Math.Max(0, (long)delay.TotalMilliseconds))), ("interrupted", interrupted || deferred ? 1 : 0), ("id", jobId.ToString()));
            await FinishAttemptAsync(connection, transaction, job, now,
                reject ? "Rejected" : deferred ? "Deferred" : result is not null ? "Succeeded" : interrupted ? "Interrupted" : "Failed", error, ct);
            if (failure is not null)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO dj_failures(job_id,attempt,exception_type,message,stack_trace)
                    VALUES (@id,@attempt,@type,@message,@stack)
                    """, ct, ("id", job.Id.ToString()), ("attempt", job.Attempt), ("type", Clip(failure.ExceptionType, 500)),
                    ("message", Clip(failure.Message, 2000)), ("stack", Clip(failure.StackTrace, 32000)));
            }

            if (job.WorkflowRunId is { } flowId)
            {
                await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
            }

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
        if (jobs.Count == 0 || jobs[0].Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Skipped or JobStatus.Cancelled or JobStatus.Rejected or JobStatus.Expired)
        {
            return false;
        }

        var job = jobs[0];
        var now = await NowAsync(connection, transaction, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE dj_jobs SET status='Cancelled',error='Cancellation requested',lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id", ct, ("id", jobId.ToString()));
        if (job.Status == JobStatus.Running)
        {
            await FinishAttemptAsync(connection, transaction, job, now, "Cancelled", "Cancellation requested", ct);
        }
        else
        {
            await JournalAsync(connection, transaction, job, now, "Cancelled", ct);
        }

        if (job.WorkflowRunId is { } flowId)
        {
            await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
        }

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
            {
                status = "Skipped";
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status=@status,error='Worker lease expired',available_at=@now,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ct, ("status", status), ("now", now), ("id", job.Id.ToString()));
            await FinishAttemptAsync(connection, transaction, job, now, "Abandoned", "Worker lease expired", ct);
            if (job.WorkflowRunId is { } flowId && status == "Failed")
            {
                await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
            }
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

    private static async Task<int> JournalAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, string kind, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "UPDATE dj_event_sequence SET value=value+1 WHERE id=1", ct);
        await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET terminal_at=CASE WHEN status IN ('Succeeded','Failed','Skipped','Cancelled','Rejected','Expired') THEN @now ELSE NULL END WHERE id=@id", ct, ("now", now), ("id", job.Id.ToString()));

        return await ExecuteAsync(connection, transaction, "INSERT INTO dj_events(sequence,job_id,run_id,at,kind,attempt) SELECT value,@id,@run,@now,@kind,@attempt FROM dj_event_sequence WHERE id=1", ct,
            ("id", job.Id.ToString()), ("run", job.WorkflowRunId?.ToString()), ("now", now), ("kind", kind), ("attempt", job.Attempt));
    }

    private static string? Clip(string? value, int maximum) => value is not null && value.Length > maximum ? value[..maximum] : value;

    protected virtual async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        if (limit.Maximum < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A limit cannot be negative. Zero pauses admission.");
        }

        if (limit.JobKey is not null)
        {
            _ = new JobContractId(limit.JobKey, 1);
        }
    }
}
