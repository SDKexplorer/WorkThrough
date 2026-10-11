using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Shared data mapping; concrete providers own connection and admission locking semantics.</summary>
public abstract partial class RelationalJobStore : IJobStore, IJobQueries, IJobAdministration, IWorkflowDefinitions, IJobDiagnostics, IJobSchedules, IDeploymentQueries, IProcessorRegistry
{
    /// <summary>The schema version this package writes. Providers refuse other versions.</summary>
    protected const int SchemaVersion = 5;

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

    // Events queued by each open write transaction; see FlushJournalAsync.
    private readonly ConditionalWeakTable<DbTransaction, List<Statement>> pendingEvents = new();

    protected abstract DbConnection CreateConnection();
    protected abstract string ClockSql { get; }
    protected abstract Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// A transaction for writes confined to rows an attempt or worker owns, fenced by its lease token or worker ID: renewal,
    /// handler and progress records, heartbeats. They need no admission lock; providers that can run them concurrently override this.
    /// </summary>
    protected virtual Task<DbTransaction> BeginOwnedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        BeginWriteAsync(connection, cancellationToken);

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

            // Schema 5 columns are added by name: older stores may have created these tables at any earlier version.
            await AddColumnIfMissingAsync(connection, transaction, "dj_jobs", "partition_key", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_jobs", "tenant_id", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_jobs", "trace_parent", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_flows", "max_parallel", "INTEGER", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_queue_controls", "weight", "INTEGER NOT NULL DEFAULT 1", ct);

            // Indexes on upgraded columns are created after the columns exist.
            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS ix_dj_jobs_terminal ON dj_jobs(status,terminal_at,id);
                CREATE INDEX IF NOT EXISTS ix_dj_jobs_partition ON dj_jobs(partition_key,status,lease_expires_at) WHERE partition_key IS NOT NULL;
                CREATE INDEX IF NOT EXISTS ix_dj_jobs_tenant ON dj_jobs(tenant_id,status,lease_expires_at) WHERE tenant_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS ix_dj_flows_bounded ON dj_flows(status) WHERE max_parallel IS NOT NULL;
                """, ct);
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

    private async Task AddColumnIfMissingAsync(DbConnection connection, DbTransaction transaction, string table, string column, string definition, CancellationToken ct)
    {
        if (Convert.ToInt64(await ScalarAsync(connection, transaction, ColumnExistsSql, ct, ("table", table), ("column", column))) == 0)
        {
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct);
        }
    }

    public Task<Guid> EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, ct) => AcceptJobAsync(connection, transaction, job, ct), cancellationToken);

    // Work without admission keys keeps the fingerprint shape of earlier releases, so existing receipts still match.
    private static string JobFingerprint(JobRecord job) => (job.PartitionKey, job.TenantId) switch
    {
        (null, null) => Fingerprint(new { job.Contract, job.Payload, job.Queue, job.CorrelationId, job.ExpiresAt, job.CausationJobId }),
        (_, null) => Fingerprint(new { job.Contract, job.Payload, job.Queue, job.CorrelationId, job.ExpiresAt, job.CausationJobId, job.PartitionKey }),
        _ => Fingerprint(new { job.Contract, job.Payload, job.Queue, job.CorrelationId, job.ExpiresAt, job.CausationJobId, job.PartitionKey, job.TenantId })
    };

    private static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JobJson.Options))));

    private async Task<Guid> AcceptJobAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        if (JobLimits.ExceedsInlineSize(job.Payload))
        {
            throw new ArgumentException("Inline input exceeds 1 MiB.");
        }

        AdmissionKeys.Validate(job.PartitionKey, KeyScope.Partition, nameof(job));
        AdmissionKeys.Validate(job.TenantId, KeyScope.Tenant, nameof(job));

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

    public async Task<JobRecord?> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default) =>
        (await ClaimBatchAsync(request with { MaximumJobs = 1 }, cancellationToken)).FirstOrDefault();

    /// <summary>
    /// Leases jobs one at a time inside a single admission transaction. Each lease is visible to the next admission check, so
    /// a batch obeys every shared limit exactly as separate claims would; sweeps and settings are paid once per batch.
    /// </summary>
    public Task<IReadOnlyList<JobRecord>> ClaimBatchAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Claim requires a worker ID and positive lease duration.");
        }

        if (request.MaximumJobs is < 1 or > ClaimRequest.MaximumBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"A claim leases 1–{ClaimRequest.MaximumBatch} jobs.");
        }

        if (request.Contracts.Count == 0 || request.Queues.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<JobRecord>>([]);
        }

        return ClaimWhenWorthwhileAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyList<JobRecord>> ClaimWhenWorthwhileAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        var outlook = await ClaimOutlookAsync(request, cancellationToken);
        if (!outlook.Proceed)
        {
            return [];
        }

        if (outlook.Unrestricted && !outlook.SweepDue && request.Queues.Count == 1
            && await ClaimUnrestrictedAsync(request, cancellationToken) is { } claimed)
        {
            return claimed;
        }

        return await WriteAsync<IReadOnlyList<JobRecord>>(async (connection, transaction, ct) =>
        {
            var settings = await ReadClaimSettingsAsync(connection, transaction, request.WorkerId, ct);
            var now = settings.Now;
            if (settings.RegistryRevision != request.RegistryRevision)
            {
                throw new ProcessorRegistryChangedException();
            }

            // Sweeps run when the snapshot saw something to sweep; anything newer is swept by the next claim.
            if (outlook.SweepDue)
            {
                await RecoverExpiredAsync(connection, transaction, now, ct);
                await ExpireQueuedAsync(connection, transaction, now, ct);
                await DispatchSignalsCoreAsync(connection, transaction, now, SweepBatchSize, ct);
            }

            var leased = new List<JobRecord>();
            if (settings.Draining)
            {
                return leased;
            }

            var budgets = new LocalBudgets(request.Budgets);
            while (leased.Count < request.MaximumJobs && !await GlobalLimitReachedAsync(connection, transaction, settings.GlobalMaximum, now, ct))
            {
                var contracts = request.Contracts.Where(contract => budgets.Allows(contract.Key)).ToArray();
                if (contracts.Length == 0 || await LeaseNextAsync(connection, transaction, request with { Contracts = contracts }, settings, now, ct) is not { } job)
                {
                    break;
                }

                leased.Add(job);
                budgets.Spend(job.Contract.Key);
            }

            return leased;
        }, cancellationToken);
    }

    /// <summary>
    /// Claims a page of ready jobs outside the admission lock while nothing needs it: no limit, rate or bounded flow, no sweep, one
    /// queue. Each job is row-locked and jobs another claim holds are skipped, so concurrent claims lease disjoint jobs. Returns null,
    /// for the serialized path, when policies appeared since the outlook.
    /// </summary>
    private Task<IReadOnlyList<JobRecord>?> ClaimUnrestrictedAsync(ClaimRequest request, CancellationToken cancellationToken) =>
        RowLockedWriteAsync<IReadOnlyList<JobRecord>?>(async (connection, transaction, ct) =>
        {
            var settings = await ReadClaimSettingsAsync(connection, transaction, request.WorkerId, ct);
            if (settings.RegistryRevision != request.RegistryRevision)
            {
                throw new ProcessorRegistryChangedException();
            }

            if (settings.Draining)
            {
                return [];
            }

            if (!settings.Unrestricted)
            {
                return null;
            }

            var budgets = new LocalBudgets(request.Budgets);
            var leased = new List<JobRecord>();
            var progressed = true;
            while (progressed && leased.Count < request.MaximumJobs)
            {
                // A page can exhaust a local budget part way; the next page asks only for contracts that still have slots.
                var contracts = request.Contracts.Where(contract => budgets.Allows(contract.Key)).ToArray();
                if (contracts.Length == 0)
                {
                    break;
                }

                var parameters = new List<(string, object?)> { ("now", settings.Now), ("queue", request.Queues.Single()) };
                var candidates = await ReadJobsAsync(connection, transaction,
                    SkipLockedJobsSql(ReadyJobSql(contracts, parameters), ClaimOrderSql, request.MaximumJobs - leased.Count), ct, parameters.ToArray());
                progressed = false;
                foreach (var candidate in candidates.OrderBy(job => job.AvailableAt).ThenBy(job => job.CreatedAt))
                {
                    if (budgets.Allows(candidate.Contract.Key) && await LeaseAsync(connection, transaction, candidate, request, settings.Now, ct) is { } job)
                    {
                        leased.Add(job);
                        budgets.Spend(job.Contract.Key);
                        progressed = true;
                    }
                }
            }

            return leased;
        }, cancellationToken);

    private const string ClaimOrderSql = "j.available_at,j.created_at,j.id";

    /// <summary>Ready jobs of the given contracts in queue @queue as of @now, whose queue is not paused and whose flow is running.</summary>
    private static string ReadyJobSql(IEnumerable<JobContractId> contracts, List<(string, object?)> parameters)
    {
        var matches = contracts.Select((contract, index) =>
        {
            parameters.Add(($"key{index}", contract.Key));
            parameters.Add(($"version{index}", contract.Version));

            return $"(j.job_key=@key{index} AND j.contract_version=@version{index})";
        }).ToArray();

        return $"""
            j.status='Queued' AND j.available_at<=@now AND (j.expires_at IS NULL OR j.expires_at>@now)
              AND NOT EXISTS (SELECT 1 FROM dj_queue_controls q WHERE q.queue=j.queue AND q.paused=1)
              AND (j.workflow_run_id IS NULL OR EXISTS (
                SELECT 1 FROM dj_flows f WHERE f.id=j.workflow_run_id AND f.status='Running'))
              AND ({string.Join(" OR ", matches)}) AND j.queue=@queue
            """;
    }

    /// <summary>The worker's remaining local slots per job group; a job outside every group is unconstrained.</summary>
    private sealed class LocalBudgets(IEnumerable<ContractBudget> budgets)
    {
        private readonly List<(HashSet<string> Keys, int Remaining)> remaining =
            budgets.Select(budget => (budget.JobKeys.ToHashSet(StringComparer.Ordinal), budget.Remaining)).ToList();

        public bool Allows(string jobKey) => remaining.All(budget => budget.Remaining > 0 || !budget.Keys.Contains(jobKey));

        public void Spend(string jobKey)
        {
            for (var index = 0; index < remaining.Count; index++)
            {
                if (remaining[index].Keys.Contains(jobKey))
                {
                    remaining[index] = remaining[index] with { Remaining = remaining[index].Remaining - 1 };
                }
            }
        }
    }

    /// <summary>Leases the next admissible job from the worker's queues in fair-share order, or returns null.</summary>
    private async Task<JobRecord?> LeaseNextAsync(DbConnection connection, DbTransaction transaction, ClaimRequest request, ClaimSettings settings,
        long now, CancellationToken ct)
    {
        var order = await FairQueueOrderAsync(connection, transaction, request, now, ct);
        foreach (var queue in order.Queues)
        {
            JobRecord? leased = null;
            var started = now;
            while (leased is null && await FindClaimableAsync(connection, transaction, request, queue, settings, now, ct) is { } candidate)
            {
                // Settlements commit outside the admission lock, so the attempt starts on a clock read after the admission counts.
                // It then never appears to overlap an attempt that those counts already saw finish.
                started = await NowAsync(connection, transaction, ct);
                leased = await LeaseAsync(connection, transaction, candidate, request, started, ct);
            }

            if (leased is null)
            {
                continue;
            }

            if (order.Competing)
            {
                await AdvanceQueueTurnAsync(connection, transaction, queue, order.Clock, ct);
            }

            if (settings.HasRateLimits)
            {
                await RecordRateStartAsync(connection, transaction, leased.Id, started, ct);
            }

            return leased;
        }

        return null;
    }

    private async Task<bool> GlobalLimitReachedAsync(DbConnection connection, DbTransaction transaction, long? maximum, long now, CancellationToken ct)
    {
        if (maximum is null)
        {
            return false;
        }

        var active = Convert.ToInt64(await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now", ct, ("now", now)));

        return active >= maximum;
    }

    private async Task<JobRecord?> FindClaimableAsync(DbConnection connection, DbTransaction transaction, ClaimRequest request, string queue,
        ClaimSettings settings, long now, CancellationToken ct)
    {
        var parameters = new List<(string, object?)> { ("now", now), ("all", AllJobsLimitKey), ("queue", queue) };
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
            WHERE {ReadyJobSql(request.Contracts, parameters)}
              AND {AdmissionGroupSql("j.job_key")} NOT IN (SELECT group_key FROM blocked_groups)
              {AdmissionPredicatesSql("j", settings)}
            ORDER BY {ClaimOrderSql} {PageSql(1)}
            """;

        return (await ReadJobsAsync(connection, transaction, sql, ct, parameters.ToArray())).SingleOrDefault();
    }

    /// <summary>
    /// Leases a candidate that is still queued. A concurrent settlement can skip a queued step of its failing flow between the candidate
    /// read and this update; the candidate is then lost and the caller looks again.
    /// </summary>
    private async Task<JobRecord?> LeaseAsync(DbConnection connection, DbTransaction transaction, JobRecord candidate, ClaimRequest request, long now, CancellationToken ct)
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
        if (await ExecuteAsync(connection, transaction, """
            UPDATE dj_jobs SET status='Running',attempt=attempt+1,worker_id=@worker,
            lease_token=@token,lease_expires_at=@expires,error=NULL WHERE id=@id AND status='Queued'
            """, ct, ("id", candidate.Id.ToString()), ("worker", request.WorkerId), ("token", token.ToString()), ("expires", expires)) == 0)
        {
            return null;
        }

        await ExecuteBatchAsync(connection, transaction, ct,
        [
            new($"""
                INSERT INTO dj_attempts(job_id,{AttemptNumberSql},worker_id,started_at,status)
                VALUES (@id,@number,@worker,@now,@status)
                """, ("id", candidate.Id.ToString()), ("number", leased.Attempt), ("worker", request.WorkerId), ("now", now), ("status", nameof(AttemptStatus.Running))),
            .. JournalStatements(transaction, leased, now, ExecutionEventKinds.Started)
        ]);

        return leased;
    }

    public Task<bool> RenewAsync(Guid jobId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return OwnedWriteAsync(async (connection, transaction, ct) =>
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
        RowLockedWriteAsync(async (connection, transaction, ct) =>
        {
            await LockFlowsOfAsync(connection, transaction, [jobId], ct);

            return await SettleCoreAsync(connection, transaction, jobId, token, settlement, await NowAsync(connection, transaction, ct), ct);
        }, cancellationToken);

    /// <summary>
    /// Settles successful attempts in one write transaction: one lock acquisition and one commit for the batch. Each item keeps
    /// its own lease check; a stale item reports false without affecting the others.
    /// </summary>
    public Task<IReadOnlyList<bool>> CompleteBatchAsync(IReadOnlyList<AttemptCompletion> completions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completions);
        foreach (var completion in completions)
        {
            completion.Result.Validate();
        }

        return RowLockedWriteAsync<IReadOnlyList<bool>>(async (connection, transaction, ct) =>
        {
            await LockFlowsOfAsync(connection, transaction, completions.Select(completion => completion.JobId), ct);
            var now = await NowAsync(connection, transaction, ct);
            var settled = new bool[completions.Count];
            for (var index = 0; index < completions.Count; index++)
            {
                var completion = completions[index];
                settled[index] = await SettleCoreAsync(connection, transaction, completion.JobId, completion.LeaseToken, new(Result: completion.Result), now, ct);
            }

            return settled;
        }, cancellationToken);
    }

    /// <summary>Settles one attempt. The caller has locked the job's flow; the job row is locked here and rechecked against the lease.</summary>
    private async Task<bool> SettleCoreAsync(DbConnection connection, DbTransaction transaction, Guid jobId, Guid token, Settlement settlement, long now,
        CancellationToken ct)
    {
        var job = (await ReadJobsAsync(connection, transaction,
            LockingSelectSql("*", "dj_jobs", "id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", skipLocked: false), ct,
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
        await ExecuteBatchAsync(connection, transaction, ct,
        [
            new("""
                UPDATE dj_jobs SET status=@status,output=@output,error=@error,available_at=@available,max_attempts=max_attempts+@granted,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ("status", status.ToString()), ("output", status == JobStatus.Succeeded ? settlement.Result?.Output.GetRawText() : null),
                ("error", Clip(error, JobLimits.MaximumMessageLength)), ("available", checked(now + Math.Max(0, (long)delay.TotalMilliseconds))),
                ("granted", grantedAttempts), ("id", jobId.ToString())),
            .. FinishAttemptStatements(transaction, job, now, attempt, error)
        ]);
        if (settlement.Failure is { } failure)
        {
            await SaveFailureAsync(connection, transaction, job, failure, ct);
        }

        if (job.WorkflowRunId is { } flowId)
        {
            await AdvanceWorkflowAsync(connection, transaction, flowId, ct);
        }

        return true;
    }

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
        var job = await ReadJobForUpdateAsync(connection, transaction, jobId, ct);
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

    public Task RecordHandlerAsync(Guid jobId, Guid leaseToken, string handler, CancellationToken cancellationToken = default) => OwnedWriteAsync(async (connection, transaction, ct) =>
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
            // Renewal and settlement run without the admission lock. A flow or lease that another transaction holds is left to the next
            // sweep rather than awaited, and the lease must still be expired and unchanged when it is revoked.
            if (job.WorkflowRunId is { } run && !await LockFlowAsync(connection, transaction, run, skipLocked: true, ct)
                || await ScalarAsync(connection, transaction,
                    LockingSelectSql("id", "dj_jobs", "id=@id AND status='Running' AND lease_token=@token AND lease_expires_at<=@now", skipLocked: true), ct,
                    ("id", job.Id.ToString()), ("token", job.LeaseToken?.ToString()), ("now", now)) is null)
            {
                continue;
            }

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

    private Task FinishAttemptAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, AttemptStatus status, string? error, CancellationToken ct) =>
        ExecuteBatchAsync(connection, transaction, ct, FinishAttemptStatements(transaction, job, now, status, error));

    private Statement[] FinishAttemptStatements(DbTransaction transaction, JobRecord job, long now, AttemptStatus status, string? error) =>
    [
        new($"UPDATE dj_attempts SET finished_at=@now,status=@status,error=@error WHERE job_id=@id AND {AttemptNumberSql}=@number AND status='Running'",
            ("now", now), ("status", status.ToString()), ("error", Clip(error, JobLimits.MaximumMessageLength)), ("id", job.Id.ToString()), ("number", job.Attempt)),
        .. JournalStatements(transaction, job, now, status.ToString())
    ];

    private Task JournalAsync(DbConnection connection, DbTransaction transaction, JobRecord job, long now, string kind, CancellationToken ct) =>
        ExecuteBatchAsync(connection, transaction, ct, JournalStatements(transaction, job, now, kind));

    /// <summary>
    /// Refreshes the job's terminal timestamp now and queues its event for the end of the transaction. Every journaled
    /// transaction increments the single sequence row, which then stays locked until commit, so it is taken as late as possible.
    /// </summary>
    private Statement[] JournalStatements(DbTransaction transaction, JobRecord job, long now, string kind)
    {
        pendingEvents.GetOrCreateValue(transaction).Add(
            new("INSERT INTO dj_events(sequence,job_id,run_id,at,kind,attempt) SELECT value,@id,@run,@now,@kind,@attempt FROM dj_event_sequence WHERE id=1",
                ("id", job.Id.ToString()), ("run", job.WorkflowRunId?.ToString()), ("now", now), ("kind", kind), ("attempt", job.Attempt)));

        return
        [
            new("""
                UPDATE dj_jobs SET terminal_at=CASE WHEN status IN ('Succeeded','Failed','Skipped','Cancelled','Rejected','Expired') THEN @now ELSE NULL END
                WHERE id=@id
                """, ("now", now), ("id", job.Id.ToString()))
        ];
    }

    /// <summary>Numbers and appends the transaction's queued events in one round trip, in the order they occurred.</summary>
    private async Task FlushJournalAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (!pendingEvents.TryGetValue(transaction, out var events))
        {
            return;
        }

        pendingEvents.Remove(transaction);
        var increment = new Statement("UPDATE dj_event_sequence SET value=value+1 WHERE id=1");
        await ExecuteBatchAsync(connection, transaction, ct, events.SelectMany(insert => new[] { increment, insert }).ToArray());
    }

    /// <summary>One SQL statement and its parameters, for sending several in a single round trip.</summary>
    private sealed record Statement(string Sql, params (string Name, object? Value)[] Values);

    /// <summary>Suffix that keeps each batched statement's parameters distinct; providers strip it to recover the original name.</summary>
    protected const string BatchParameterSuffix = "__b";

    protected static string BaseParameterName(string name) =>
        name.IndexOf(BatchParameterSuffix, StringComparison.Ordinal) is var index and >= 0 ? name[..index] : name;

    /// <summary>Joins statements that run in order in one command; providers without multi-statement commands wrap them.</summary>
    protected virtual string BatchSql(IReadOnlyList<string> statements) => string.Join(";\n", statements);

    /// <summary>
    /// Sends consecutive statements of the current transaction in one round trip. Admission holds the write lock while they
    /// run, so every round trip saved shortens the serialized section for all writers.
    /// </summary>
    private Task<int> ExecuteBatchAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct, params Statement[] statements)
    {
        if (statements.Length == 1)
        {
            return ExecuteAsync(connection, transaction, statements[0].Sql, ct, statements[0].Values);
        }

        var sql = new string[statements.Length];
        var values = new List<(string, object?)>();
        for (var index = 0; index < statements.Length; index++)
        {
            var suffix = BatchParameterSuffix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sql[index] = BatchParameters.Replace(statements[index].Sql, match => match.Value + suffix);
            values.AddRange(statements[index].Values.Select(value => (value.Name + suffix, value.Value)));
        }

        return ExecuteAsync(connection, transaction, BatchSql(sql), ct, values.ToArray());
    }

    private static readonly System.Text.RegularExpressions.Regex BatchParameters = new("@[A-Za-z][A-Za-z0-9_]*", System.Text.RegularExpressions.RegexOptions.Compiled);

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

    protected Task<T> WriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct) =>
        TransactAsync(BeginWriteAsync, action, ct);

    /// <summary>Runs writes confined to owned rows; see <see cref="BeginOwnedWriteAsync"/>.</summary>
    protected Task<T> OwnedWriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct) =>
        TransactAsync(BeginOwnedWriteAsync, action, ct);

    private async Task<T> TransactAsync<T>(Func<DbConnection, CancellationToken, Task<DbTransaction>> begin,
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await begin(connection, ct).ConfigureAwait(false);
        var result = await action(connection, transaction, ct).ConfigureAwait(false);
        await FlushJournalAsync(connection, transaction, ct).ConfigureAwait(false);
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
