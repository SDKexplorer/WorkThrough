using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Shared data mapping; concrete providers own connection and admission locking semantics.</summary>
public abstract partial class RelationalJobStore : IJobStore, IJobQueries, IJobAdministration, IWorkflowDefinitions, IJobDiagnostics, IJobSchedules, IDeploymentQueries, IProcessorRegistry
{
    /// <summary>The schema version this package writes. Providers refuse other versions.</summary>
    protected const int SchemaVersion = 4;

    // Version 3 added expiry, causation and terminal timestamps to existing job tables.
    private const int TerminalTimestampsVersion = 3;

    /// <summary>Stored limit key for the cluster-wide limit, represented by a null <see cref="ConcurrencyLimit.JobKey"/>.</summary>
    private const string AllJobsLimitKey = "*";

    // Expired leases and queued expirations are recovered in bounded slices inside each admission transaction.
    private const int SweepBatchSize = 100;
    private const int MaximumExceptionTypeLength = 500;
    private const int MaximumStackTraceLength = 32000;
    private const string LeaseExpiredError = "Worker lease expired";
    private const string CancellationError = "Cancellation requested";
    private const int MaximumReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(25);

    protected abstract DbConnection CreateConnection();
    protected abstract string ClockSql { get; }
    protected abstract Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken);

    public virtual async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(async (connection, transaction, ct) =>
        {
            await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS dj_schema (version INTEGER NOT NULL PRIMARY KEY)", ct);
            var stored = await ScalarAsync(connection, transaction, "SELECT MAX(version) FROM dj_schema", ct);
            int? version = stored is null or DBNull ? null : Convert.ToInt32(stored);
            if (version is < 1 or > SchemaVersion)
            {
                throw new InvalidOperationException("Unsupported WorkThrough schema version. Use a compatible package and migration.");
            }

            await ExecuteAsync(connection, transaction, Schema, ct);
            if (version < TerminalTimestampsVersion)
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE dj_jobs ADD COLUMN expires_at BIGINT; ALTER TABLE dj_jobs ADD COLUMN causation_job_id TEXT; ALTER TABLE dj_jobs ADD COLUMN terminal_at BIGINT", ct);
                await ExecuteAsync(connection, transaction, """
                    UPDATE dj_jobs SET terminal_at=COALESCE((SELECT MAX(finished_at) FROM dj_attempts WHERE job_id=dj_jobs.id),created_at)
                    WHERE status IN ('Succeeded','Failed','Skipped','Cancelled')
                    """, ct);
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
                INSERT INTO dj_schema(version) VALUES (@version) ON CONFLICT DO NOTHING;
                """, ct, ("version", SchemaVersion));

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Guid> EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, ct) => AcceptJobAsync(connection, transaction, job, ct), cancellationToken);

    private static string JobFingerprint(JobRecord job) => Fingerprint(new
    {
        job.Contract,
        job.Payload,
        job.Queue,
        job.CorrelationId,
        job.ExpiresAt,
        job.CausationJobId
    });

    private static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JobJson.Options))));

    private async Task<Guid> AcceptJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        if (JobLimits.ExceedsInlineSize(job.Payload))
        {
            throw new ArgumentException("Inline input exceeds 1 MiB.");
        }

        if (job.IdempotencyKey is not null && await FindAcceptedAsync(connection, transaction, job, ct) is { } accepted)
        {
            return accepted;
        }

        if (job.UseStorageTime)
        {
            var acceptedAt = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            job = job with { CreatedAt = acceptedAt, AvailableAt = acceptedAt, UseStorageTime = false };
        }

        await InsertJobAsync(connection, transaction, job, ct);
        await SaveJobReceiptAsync(connection, transaction, job, ct);

        return job.Id;
    }

    /// <summary>Returns the job already accepted for this idempotency key, from its receipt or a pre-receipt row.</summary>
    private async Task<Guid?> FindAcceptedAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        var fingerprint = JobFingerprint(job);
        await using (var command = Command(connection, transaction, "SELECT fingerprint,job_id FROM dj_job_receipts WHERE idempotency_key=@key", ("key", job.IdempotencyKey)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return reader.GetString(0) == fingerprint ? Guid.Parse(reader.GetString(1)) : throw IdempotencyConflict();
            }
        }

        var existing = (await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE idempotency_key=@key", ct, ("key", job.IdempotencyKey))).SingleOrDefault();
        if (existing is null)
        {
            return null;
        }

        if (JobFingerprint(existing) != fingerprint)
        {
            throw IdempotencyConflict();
        }

        await SaveJobReceiptAsync(connection, transaction, existing, ct);

        return existing.Id;

        static StateConflictException IdempotencyConflict() => new("Idempotency key is already associated with different work.");
    }

    private Task<int> SaveJobReceiptAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct) => job.IdempotencyKey is null
        ? Task.FromResult(0)
        : ExecuteAsync(connection, transaction, UpsertSql("dj_job_receipts", "idempotency_key,fingerprint,job_id", "@key,@hash,@id", "idempotency_key"), ct,
            ("key", job.IdempotencyKey), ("hash", JobFingerprint(job)), ("id", job.Id.ToString()));

    public Task SeedLimitsAsync(IReadOnlyList<ConcurrencyLimit> limits, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        foreach (var limit in limits)
        {
            Validate(limit);
            await SaveLimitAsync(connection, transaction, limit, replace: false, ct);
        }

        return true;
    }, cancellationToken);

    public Task SetLimitAsync(ConcurrencyLimit limit, CancellationToken cancellationToken = default)
    {
        Validate(limit);

        return WriteAsync(async (connection, transaction, ct) =>
        {
            await SaveLimitAsync(connection, transaction, limit, replace: true, ct);

            return true;
        }, cancellationToken);
    }

    private Task<int> SaveLimitAsync(DbConnection connection, DbTransaction transaction, ConcurrencyLimit limit, bool replace, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, UpsertSql("dj_limits", "job_key,maximum", "@key,@maximum", "job_key", replace ? "maximum" : ""), ct,
            ("key", limit.JobKey ?? AllJobsLimitKey), ("maximum", limit.Maximum));

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
            if (await ReadRegistryRevisionAsync(connection, transaction, ct) != request.RegistryRevision)
            {
                throw new ProcessorRegistryChangedException();
            }

            await RecoverExpiredAsync(connection, transaction, now, ct);
            await ExpireQueuedAsync(connection, transaction, now, ct);
            await DispatchSignalsCoreAsync(connection, transaction, now, SweepBatchSize, ct);
            if (await IsDrainingAsync(connection, transaction, request.WorkerId, ct) || await GlobalLimitReachedAsync(connection, transaction, now, ct))
            {
                return null;
            }

            var candidate = await FindClaimableAsync(connection, transaction, request, now, ct);

            return candidate is null ? null : await LeaseAsync(connection, transaction, candidate, request, now, ct);
        }, cancellationToken);
    }

    private async Task<bool> IsDrainingAsync(DbConnection connection, DbTransaction transaction, string workerId, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_worker_controls WHERE worker_id=@worker AND draining=1", ct, ("worker", workerId))) > 0;

    private async Task<bool> GlobalLimitReachedAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct)
    {
        var maximum = await ScalarAsync(connection, transaction, "SELECT maximum FROM dj_limits WHERE job_key=@all", ct, ("all", AllJobsLimitKey));
        if (maximum is null or DBNull)
        {
            return false;
        }

        var active = Convert.ToInt64(await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now", ct, ("now", now)));

        return active >= Convert.ToInt64(maximum);
    }

    private async Task<JobRecord?> FindClaimableAsync(DbConnection connection, DbTransaction transaction, ClaimRequest request, long now, CancellationToken ct)
    {
        var parameters = new List<(string, object?)> { ("now", now), ("all", AllJobsLimitKey) };
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
              WHERE l.job_key<>@all AND l.maximum<=COALESCE(a.total,0)
            )
            SELECT j.* FROM dj_jobs j
            WHERE j.status='Queued' AND j.available_at<=@now AND (j.expires_at IS NULL OR j.expires_at>@now)
              AND NOT EXISTS (SELECT 1 FROM dj_queue_controls q WHERE q.queue=j.queue AND q.paused=1)
              AND (j.workflow_run_id IS NULL OR EXISTS (
                SELECT 1 FROM dj_flows f WHERE f.id=j.workflow_run_id AND f.status='Running'))
              AND ({string.Join(" OR ", contracts)}) AND j.queue IN ({string.Join(",", queues)})
              AND COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=j.job_key),j.job_key)
                NOT IN (SELECT group_key FROM blocked_groups)
            ORDER BY j.available_at,j.created_at,j.id {PageSql(1)}
            """;

        return (await ReadJobsAsync(connection, transaction, sql, ct, parameters.ToArray())).SingleOrDefault();
    }

    private async Task<JobRecord> LeaseAsync(DbConnection connection, DbTransaction transaction, JobRecord candidate, ClaimRequest request, long now, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var expires = checked(now + (long)request.LeaseDuration.TotalMilliseconds);
        var leased = candidate with
        {
            Status = JobStatus.Running,
            Attempt = candidate.Attempt + 1,
            WorkerId = request.WorkerId,
            LeaseToken = token,
            LeaseExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires)
        };
        await ExecuteAsync(connection, transaction, """
            UPDATE dj_jobs SET status='Running',attempt=attempt+1,worker_id=@worker,
            lease_token=@token,lease_expires_at=@expires,error=NULL WHERE id=@id
            """, ct, ("id", candidate.Id.ToString()), ("worker", request.WorkerId), ("token", token.ToString()), ("expires", expires));
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO dj_attempts(job_id,{AttemptNumberSql},worker_id,started_at,status)
            VALUES (@id,@number,@worker,@now,@status)
            """, ct, ("id", candidate.Id.ToString()), ("number", leased.Attempt), ("worker", request.WorkerId), ("now", now), ("status", nameof(AttemptStatus.Running)));
        await JournalAsync(connection, transaction, leased, now, ExecutionEventKinds.Started, ct);

        return leased;
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
        SettleAsync(jobId, leaseToken, new(Result: result), cancellationToken);

    public Task<bool> RejectAsync(Guid jobId, Guid leaseToken, JobFailure failure, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, new(Error: failure.ToSummary(), Failure: failure, Rejected: true), cancellationToken);

    public Task<bool> FailAsync(Guid jobId, Guid leaseToken, string error, TimeSpan retryDelay, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, new(Error: error, Delay: retryDelay), cancellationToken);

    public Task<bool> ReleaseAsync(Guid jobId, Guid leaseToken, string reason, CancellationToken cancellationToken = default) =>
        SettleAsync(jobId, leaseToken, new(Error: reason, Interrupted: true), cancellationToken);

    /// <summary>How the lease owner ended an attempt. Exactly one shape applies per call.</summary>
    private sealed record Settlement(JobResult? Result = null, string? Error = null, TimeSpan Delay = default,
        bool Interrupted = false, JobFailure? Failure = null, bool Rejected = false);

    private Task<bool> SettleAsync(Guid jobId, Guid token, Settlement settlement, CancellationToken cancellationToken) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            var job = (await ReadJobsAsync(connection, transaction,
                "SELECT * FROM dj_jobs WHERE id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", ct,
                ("id", jobId.ToString()), ("token", token.ToString()), ("now", now))).SingleOrDefault();
            if (job is null)
            {
                return false;
            }

            settlement.Result?.Validate();
            var attempt = SettledAttempt(settlement);
            var deferred = attempt == AttemptStatus.Deferred;
            var delay = deferred ? settlement.Result!.RetryAfter!.Value : settlement.Delay;
            var error = settlement.Error ?? settlement.Result?.Reason;
            var status = SettledJob(attempt, job);
            if (status == JobStatus.Queued && !await WorkflowAllowsRetryAsync(connection, transaction, job, ct))
            {
                status = JobStatus.Skipped;
            }

            // Interruption and deferral are not failures, so they grant back the attempt they consumed.
            var grantedAttempts = attempt is AttemptStatus.Interrupted or AttemptStatus.Deferred ? 1 : 0;
            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status=@status,output=@output,error=@error,available_at=@available,max_attempts=max_attempts+@granted,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ct, ("status", status.ToString()), ("output", status == JobStatus.Succeeded ? settlement.Result?.Output.GetRawText() : null),
                ("error", Clip(error, JobLimits.MaximumMessageLength)), ("available", checked(now + Math.Max(0, (long)delay.TotalMilliseconds))),
                ("granted", grantedAttempts), ("id", jobId.ToString()));
            await FinishAttemptAsync(connection, transaction, job, now, attempt, error, ct);
            if (settlement.Failure is { } failure)
            {
                await SaveFailureAsync(connection, transaction, job, failure, ct);
            }

            if (job.WorkflowRunId is { } flowId)
            {
                await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
            }

            return true;
        }, cancellationToken);

    private static AttemptStatus SettledAttempt(Settlement settlement) => settlement switch
    {
        { Rejected: true } or { Result.Outcome: JobOutcome.Rejected } => AttemptStatus.Rejected,
        { Result.Outcome: JobOutcome.Deferred } => AttemptStatus.Deferred,
        { Result: not null } => AttemptStatus.Succeeded,
        { Interrupted: true } => AttemptStatus.Interrupted,
        _ => AttemptStatus.Failed
    };

    private static JobStatus SettledJob(AttemptStatus attempt, JobRecord job) => attempt switch
    {
        AttemptStatus.Rejected => JobStatus.Rejected,
        AttemptStatus.Succeeded => JobStatus.Succeeded,
        AttemptStatus.Deferred or AttemptStatus.Interrupted => JobStatus.Queued,
        _ => job.Attempt < job.MaxAttempts ? JobStatus.Queued : JobStatus.Failed
    };

    private Task<int> SaveFailureAsync(DbConnection connection, DbTransaction transaction, JobRecord job, JobFailure failure, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO dj_failures(job_id,attempt,exception_type,message,stack_trace)
            VALUES (@id,@attempt,@type,@message,@stack)
            """, ct, ("id", job.Id.ToString()), ("attempt", job.Attempt), ("type", Clip(failure.ExceptionType, MaximumExceptionTypeLength)),
            ("message", Clip(failure.Message, JobLimits.MaximumMessageLength)), ("stack", Clip(failure.StackTrace, MaximumStackTraceLength)));

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
        var job = await ReadJobAsync(connection, transaction, jobId, ct);
        if (job is null || job.Status.IsTerminal())
        {
            return false;
        }

        var now = await NowAsync(connection, transaction, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE dj_jobs SET status='Cancelled',error=@error,lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id", ct,
            ("error", CancellationError), ("id", jobId.ToString()));
        if (job.Status == JobStatus.Running)
        {
            await FinishAttemptAsync(connection, transaction, job, now, AttemptStatus.Cancelled, CancellationError, ct);
        }
        else
        {
            await JournalAsync(connection, transaction, job, now, nameof(JobStatus.Cancelled), ct);
        }

        if (job.WorkflowRunId is { } flowId)
        {
            await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
        }

        return true;
    }

    public Task RecordHandlerAsync(Guid jobId, Guid leaseToken, string handler, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        await ExecuteAsync(connection, transaction, $"""
            UPDATE dj_attempts SET handler=@handler WHERE job_id=@id AND status='Running'
            AND {AttemptNumberSql}=(SELECT attempt FROM dj_jobs WHERE id=@id AND lease_token=@token)
            """, ct, ("handler", handler), ("id", jobId.ToString()), ("token", leaseToken.ToString()));

        return true;
    }, cancellationToken);

    private async Task RecoverExpiredAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct)
    {
        var expired = await ReadJobsAsync(connection, transaction,
            $"SELECT * FROM dj_jobs WHERE status='Running' AND lease_expires_at<=@now ORDER BY lease_expires_at {PageSql(SweepBatchSize)}", ct, ("now", now));
        foreach (var job in expired)
        {
            var status = job.Attempt < job.MaxAttempts ? JobStatus.Queued : JobStatus.Failed;
            if (status == JobStatus.Queued && !await WorkflowAllowsRetryAsync(connection, transaction, job, ct))
            {
                status = JobStatus.Skipped;
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE dj_jobs SET status=@status,error=@error,available_at=@now,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ct, ("status", status.ToString()), ("error", LeaseExpiredError), ("now", now), ("id", job.Id.ToString()));
            await FinishAttemptAsync(connection, transaction, job, now, AttemptStatus.Abandoned, LeaseExpiredError, ct);
            if (job.WorkflowRunId is { } flowId && status == JobStatus.Failed)
            {
                await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
            }
        }
    }

    private async Task<bool> WorkflowAllowsRetryAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct) =>
        job.WorkflowRunId is not { } runId
        || await ReadFlowStatusAsync(connection, transaction, runId, ct) is FlowStatus.Running or FlowStatus.Paused or FlowStatus.Incident;

    private async Task<FlowStatus?> ReadFlowStatusAsync(DbConnection connection, DbTransaction? transaction, Guid runId, CancellationToken ct) =>
        await ScalarAsync(connection, transaction, "SELECT status FROM dj_flows WHERE id=@id", ct, ("id", runId.ToString())) is string status
            ? Enum.Parse<FlowStatus>(status)
            : null;

    private async Task FinishAttemptAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, AttemptStatus status, string? error, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            $"UPDATE dj_attempts SET finished_at=@now,status=@status,error=@error WHERE job_id=@id AND {AttemptNumberSql}=@number AND status='Running'", ct,
            ("now", now), ("status", status.ToString()), ("error", Clip(error, JobLimits.MaximumMessageLength)), ("id", job.Id.ToString()), ("number", job.Attempt));
        await JournalAsync(connection, transaction, job, now, status.ToString(), ct);
    }

    private async Task<int> JournalAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, string kind, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "UPDATE dj_event_sequence SET value=value+1 WHERE id=1", ct);
        await ExecuteAsync(connection, transaction, """
            UPDATE dj_jobs SET terminal_at=CASE WHEN status IN ('Succeeded','Failed','Skipped','Cancelled','Rejected','Expired') THEN @now ELSE NULL END
            WHERE id=@id
            """, ct, ("now", now), ("id", job.Id.ToString()));

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

    protected async Task<T> WriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await BeginWriteAsync(connection, ct).ConfigureAwait(false);
        var result = await action(connection, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>Runs a read in the provider's read snapshot, retrying with a fresh snapshot after a transient snapshot conflict.</summary>
    /// <remarks>Auto-commit reads take shared locks on SQL Server and can be chosen as deadlock victims by concurrent settlement.
    /// Reads change nothing, so repeating one is safe. Writes are never retried.</remarks>
    protected async Task<T> ReadAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadSnapshotAsync(read, ct).ConfigureAwait(false);
            }
            catch (DbException error) when (attempt < MaximumReadAttempts && IsReadSnapshotConflict(error))
            {
                await Task.Delay(ReadRetryDelay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A read failure that a new snapshot can resolve, such as Oracle's ORA-08177 after concurrent dictionary DDL.</summary>
    protected virtual bool IsReadSnapshotConflict(DbException error) => false;

    private async Task<T> ReadSnapshotAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await BeginReadAsync(connection, ct).ConfigureAwait(false);
        var result = await read(connection, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return result;
    }

    private async Task<long> NowAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction, ClockSql, ct).ConfigureAwait(false));

    protected virtual DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] values)
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

    protected async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, sql, values);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected async Task<object?> ScalarAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, sql, values);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static void Validate(ConcurrencyLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
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
