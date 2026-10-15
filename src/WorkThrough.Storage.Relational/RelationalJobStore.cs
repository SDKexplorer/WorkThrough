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
    protected const int SchemaVersion = 11;

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

    static RelationalJobStore() => JobJson.AddContext(StorageJsonContext.Default);

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
            if (version is < 1)
            {
                throw new InvalidOperationException("Unsupported WorkThrough schema version. Use a compatible package and migration.");
            }

            if (version is { } existing && await UsesNewerSchemaAsync(connection, transaction, existing, ct))
            {
                return true;
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

            // Schema 8 links child runs to their parent step (ADR-023).
            await AddColumnIfMissingAsync(connection, transaction, "dj_flows", "parent_run_id", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_flows", "parent_job_id", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_flows", "depth", "INTEGER", ct);

            // Schema 9 keeps the first input of a corrected job.
            await AddColumnIfMissingAsync(connection, transaction, "dj_jobs", "original_payload", "TEXT", ct);

            // Schema 10 links a forked run to its source.
            await AddColumnIfMissingAsync(connection, transaction, "dj_flows", "forked_from_run_id", "TEXT", ct);
            await AddColumnIfMissingAsync(connection, transaction, "dj_queue_controls", "weight", "INTEGER NOT NULL DEFAULT 1", ct);

            // Indexes on upgraded columns are created after the columns exist.
            await ExecuteAsync(connection, transaction, """
                CREATE INDEX IF NOT EXISTS ix_dj_jobs_terminal ON dj_jobs(status,terminal_at,id);
                CREATE INDEX IF NOT EXISTS ix_dj_jobs_partition ON dj_jobs(partition_key,status,lease_expires_at) WHERE partition_key IS NOT NULL;
                CREATE INDEX IF NOT EXISTS ix_dj_jobs_tenant ON dj_jobs(tenant_id,status,lease_expires_at) WHERE tenant_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS ix_dj_flows_bounded ON dj_flows(status) WHERE max_parallel IS NOT NULL;
                CREATE INDEX IF NOT EXISTS ix_dj_flows_parent ON dj_flows(parent_run_id) WHERE parent_run_id IS NOT NULL;
                """, ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO dj_event_sequence(id,value) SELECT 1,COALESCE(MAX(sequence),0) FROM dj_events WHERE 1=1 ON CONFLICT DO NOTHING", ct);
            if (Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_store", ct)) == 0)
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_store(id) VALUES (@id)", ct, ("id", Guid.NewGuid().ToString("N")));
            }

            await SeedCompatibilityAsync(connection, transaction, newStore: version is null, ct);
            await SeedLimitLocksAsync(connection, transaction, ct);
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
        (null, null) => Fingerprint(Args(("contract", job.Contract), ("payload", job.Payload), ("queue", job.Queue), ("correlationId", job.CorrelationId), ("expiresAt", job.ExpiresAt), ("causationJobId", job.CausationJobId))),
        (_, null) => Fingerprint(Args(("contract", job.Contract), ("payload", job.Payload), ("queue", job.Queue), ("correlationId", job.CorrelationId), ("expiresAt", job.ExpiresAt), ("causationJobId", job.CausationJobId), ("partitionKey", job.PartitionKey))),
        _ => Fingerprint(Args(("contract", job.Contract), ("payload", job.Payload), ("queue", job.Queue), ("correlationId", job.CorrelationId), ("expiresAt", job.ExpiresAt), ("causationJobId", job.CausationJobId), ("partitionKey", job.PartitionKey), ("tenantId", job.TenantId)))
    };

    private static string Fingerprint(Fields value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JobJson.Serialize(value))));

    /// <summary>
    /// A JSON object with these properties in this order. It stands in for the anonymous objects earlier releases serialized, with the
    /// same names and values, so their fingerprints and audit records keep their exact form.
    /// </summary>
    private static Fields Args(params (string Name, object? Value)[] properties) => Fields.Named(properties);

    private static Fields Arguments(object value) => Fields.Of(value);

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

        // From protocol 3 every claim takes row locks, so sweeps run in a short admission transaction of their own.
        var concurrentLimits = outlook.Protocol >= EngineProtocol.ConcurrentLimits;
        if (concurrentLimits && outlook.SweepDue)
        {
            await WriteAsync(async (connection, transaction, ct) =>
            {
                await SweepAsync(connection, transaction, await NowAsync(connection, transaction, ct), ct);

                return true;
            }, cancellationToken);
        }

        if ((concurrentLimits || outlook.Unrestricted && !outlook.SweepDue && request.Queues.Count == 1)
            && await ClaimWithRowLocksAsync(request, cancellationToken) is { } claimed)
        {
            return claimed;
        }

        return await WriteAsync<IReadOnlyList<JobRecord>>(async (connection, transaction, ct) =>
        {
            var settings = await ReadClaimSettingsAsync(connection, transaction, request.WorkerId, ct);
            var now = settings.Now;
            settings.RequireClaimable(request);

            // Sweeps run when the snapshot saw something to sweep; anything newer is swept by the next claim.
            if (outlook.SweepDue)
            {
                await SweepAsync(connection, transaction, now, ct);
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

    /// <summary>Recovers expired leases, expires overdue queued work, resolves due signals and drops rate starts no window counts.</summary>
    private async Task SweepAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct)
    {
        await RecoverExpiredAsync(connection, transaction, now, ct);
        await ExpireQueuedAsync(connection, transaction, now, ct);
        await DispatchSignalsCoreAsync(connection, transaction, now, SweepBatchSize, ct);
        await PruneRateLedgerAsync(connection, transaction, now, ct);
    }

    /// <summary>
    /// Claims outside the admission lock. Candidates are row-locked and jobs another claim holds are skipped, so concurrent claims
    /// lease disjoint jobs. Under shared limits, the claim also locks the limit rows of its candidates and counts admitted leases
    /// again before each lease (ADR-022). Below engine protocol 3 this runs only while no limit exists, for one queue; it returns
    /// null, for the serialized path, when that no longer holds.
    /// </summary>
    private async Task<IReadOnlyList<JobRecord>?> ClaimWithRowLocksAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        var exclusions = new ClaimExclusions();
        var deadlocks = 0;
        for (var attempt = 1; ; attempt++)
        {
            IReadOnlyList<JobRecord>? claimed;
            try
            {
                claimed = await ClaimWithRowLocksOnceAsync(request, exclusions, cancellationToken);
            }
            catch (DbException error) when (IsDeadlockVictim(error))
            {
                // The database rolled the whole claim back, so it leased nothing. Under heavy contention SQL Server picks victims
                // repeatedly; back off a little at random and try again, and after that report nothing claimed: the worker polls again.
                if (++deadlocks > MaximumDeadlockRetries)
                {
                    return [];
                }

                attempt--;
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(DeadlockBackoffMilliseconds) * deadlocks), cancellationToken);

                continue;
            }

            if (!exclusions.Restart || attempt == MaximumClaimRestarts)
            {
                return claimed;
            }
        }
    }

    /// <summary>
    /// One claim transaction. A page after the first must not wait for limit locks while this transaction holds others, so when it
    /// cannot take them and nothing is leased yet, the claim asks to restart: ending the transaction releases the candidates it held,
    /// which other claims skipped meanwhile, and the next attempt keeps what it learned about rejected jobs and full flows.
    /// </summary>
    private Task<IReadOnlyList<JobRecord>?> ClaimWithRowLocksOnceAsync(ClaimRequest request, ClaimExclusions exclusions, CancellationToken cancellationToken) =>
        RowLockedWriteAsync<IReadOnlyList<JobRecord>?>(async (connection, transaction, ct) =>
        {
            exclusions.Restart = false;
            var settings = await ReadClaimSettingsAsync(connection, transaction, request.WorkerId, ct);
            settings.RequireClaimable(request);
            if (settings.Draining)
            {
                return [];
            }

            var competing = request.Queues.Count > 1;
            if (settings.Protocol < EngineProtocol.ConcurrentLimits && (!settings.Unrestricted || competing))
            {
                return null;
            }

            var policies = await ReadLimitPoliciesAsync(connection, transaction, settings, ct);
            if (competing)
            {
                // Claims for several queues serialize on the queue clock, which keeps their weighted turns exact.
                await ScalarAsync(connection, transaction, LockingSelectSql("id", "dj_queue_clock", "id=1", skipLocked: false), ct);
            }

            var budgets = new LocalBudgets(request.Budgets);
            var leased = new List<JobRecord>();
            var rejected = exclusions.Rejected;
            var fullFlows = exclusions.FullFlows;
            var fruitless = 0;
            var holdsLimitLocks = false;
            while (leased.Count < request.MaximumJobs && fruitless < MaximumFruitlessPages)
            {
                // A page can exhaust a local budget part way; the next page asks only for contracts that still have slots.
                var contracts = request.Contracts.Where(contract => budgets.Allows(contract.Key)).ToArray();
                if (contracts.Length == 0 || await GlobalLimitReachedAsync(connection, transaction, settings.GlobalMaximum, settings.Now, ct))
                {
                    break;
                }

                // Competing queues lease one job per turn; a single queue leases a page.
                var order = await FairQueueOrderAsync(connection, transaction, request, settings.Now, ct);
                JobRecord[] candidates = [];
                foreach (var queue in order.Queues)
                {
                    candidates = await ReadCandidatesAsync(connection, transaction, contracts, queue, settings, competing ? 1 : request.MaximumJobs - leased.Count,
                        rejected, fullFlows, ct);
                    if (candidates.Length > 0)
                    {
                        break;
                    }
                }

                if (candidates.Length == 0)
                {
                    break;
                }

                if (settings.Protocol >= EngineProtocol.ConcurrentLimits)
                {
                    var rows = candidates.SelectMany(candidate => LimitScopes(candidate, Admitting(candidate, settings), policies)).Select(LimitLockRow).ToArray();
                    if (!await LockLimitRowsAsync(connection, transaction, rows, wait: !holdsLimitLocks, ct))
                    {
                        exclusions.Restart = leased.Count == 0;

                        break;
                    }

                    holdsLimitLocks |= rows.Length > 0;
                }

                var progressed = false;
                foreach (var candidate in candidates)
                {
                    if (leased.Count >= request.MaximumJobs || !budgets.Allows(candidate.Contract.Key))
                    {
                        continue;
                    }

                    if (await LeaseAdmittedAsync(connection, transaction, request, candidate, settings, ct) is not { } job)
                    {
                        rejected.Add(candidate.Id);

                        // A full flow would refuse its other steps too; skipping them keeps its backlog from using up this poll's pages.
                        if (candidate.WorkflowRunId is { } run && await FlowAtBoundAsync(connection, transaction, candidate.Id, settings.Now, ct))
                        {
                            fullFlows.Add(run);
                        }

                        continue;
                    }

                    leased.Add(job);
                    budgets.Spend(job.Contract.Key);
                    progressed = true;
                    if (order.Competing)
                    {
                        await AdvanceQueueTurnAsync(connection, transaction, job.Queue, order.Clock, ct);
                    }
                }

                // The snapshot pre-filter can offer candidates that the exact check rejects; later pages may still hold admissible work.
                fruitless = progressed ? 0 : fruitless + 1;
            }

            return leased;
        }, cancellationToken);

    /// <summary>
    /// The policies a candidate is admitted under. A flow's step parallelism is set when the flow starts, which a claim's settings
    /// snapshot can miss, so from protocol 3 every flow step is checked against its flow's bound and takes that flow's lock row.
    /// </summary>
    private static ClaimSettings Admitting(JobRecord candidate, ClaimSettings settings) =>
        candidate.WorkflowRunId is not null && settings.Protocol >= EngineProtocol.ConcurrentLimits ? settings with { HasBoundedFlows = true } : settings;

    /// <summary>Ready jobs of one queue in claim order, locked for this transaction and skipping jobs other claims hold.</summary>
    /// <remarks>The admission predicates narrow the page from a snapshot; the exact check runs again under the limit locks.</remarks>
    private async Task<JobRecord[]> ReadCandidatesAsync(DbConnection connection, DbTransaction transaction, IEnumerable<JobContractId> contracts, string queue,
        ClaimSettings settings, int limit, IReadOnlyCollection<Guid> rejected, IReadOnlyCollection<Guid> fullFlows, CancellationToken ct)
    {
        var parameters = new List<(string, object?)> { ("now", settings.Now), ("queue", queue), ("all", AllJobsLimitKey) };
        var predicate = ReadyJobSql(contracts, parameters) + (settings.HasTypeLimits ? $"\n  AND NOT {TypeBlockedSql("j")}" : "") + AdmissionPredicatesSql("j", settings);

        // Below protocol 3 only unrestricted claims get here, and they take no flow locks: steps of a bounded flow that started after
        // the settings snapshot wait for the serialized path.
        if (settings.Protocol < EngineProtocol.ConcurrentLimits)
        {
            predicate += "\n  AND (j.workflow_run_id IS NULL OR NOT EXISTS (SELECT 1 FROM dj_flows bf WHERE bf.id=j.workflow_run_id AND bf.max_parallel IS NOT NULL))";
        }

        if (rejected.Count > 0)
        {
            var excluded = rejected.Select((id, index) =>
            {
                parameters.Add(($"rejected{index}", id.ToString()));

                return $"@rejected{index}";
            });
            predicate += $"\n  AND j.id NOT IN ({string.Join(",", excluded)})";
        }

        if (fullFlows.Count > 0)
        {
            var excluded = fullFlows.Select((id, index) =>
            {
                parameters.Add(($"full{index}", id.ToString()));

                return $"@full{index}";
            });
            predicate += $"\n  AND (j.workflow_run_id IS NULL OR j.workflow_run_id NOT IN ({string.Join(",", excluded)}))";
        }

        var candidates = await LockCandidatesAsync(connection, transaction, predicate, limit, parameters.ToArray(), ct);

        return [.. candidates.OrderBy(job => job.AvailableAt).ThenBy(job => job.CreatedAt)];
    }

    /// <summary>
    /// Leases a locked candidate. Under limits it first repeats the exact admission check, then starts the attempt on a clock read
    /// after that check, as the serialized path does.
    /// </summary>
    private async Task<JobRecord?> LeaseAdmittedAsync(DbConnection connection, DbTransaction transaction, ClaimRequest request, JobRecord candidate,
        ClaimSettings claim, CancellationToken ct)
    {
        var settings = Admitting(candidate, claim);
        if (settings.Unrestricted)
        {
            return await LeaseAsync(connection, transaction, candidate, request, settings.Now, ct);
        }

        if (await GlobalLimitReachedAsync(connection, transaction, settings.GlobalMaximum, settings.Now, ct)
            || await FindClaimableAsync(connection, transaction, request with { Contracts = [candidate.Contract] }, candidate.Queue, settings, settings.Now, ct,
                only: candidate.Id) is null)
        {
            return null;
        }

        var started = await NowAsync(connection, transaction, ct);
        var leased = await LeaseAsync(connection, transaction, candidate, request, started, ct);
        if (leased is not null && settings.HasRateLimits)
        {
            await RecordRateStartAsync(connection, transaction, leased.Id, started, ct);
        }

        return leased;
    }

    private const string ClaimOrderSql = "j.available_at,j.created_at,j.id";

    /// <summary>Pages in a row whose every candidate failed the exact admission check before a claim gives up for this poll.</summary>
    private const int MaximumFruitlessPages = 3;

    /// <summary>Claim transactions one poll may use; see <see cref="ClaimWithRowLocksOnceAsync"/>.</summary>
    private const int MaximumClaimRestarts = 3;

    /// <summary>Times one poll retries a claim the database chose as a deadlock victim.</summary>
    private const int MaximumDeadlockRetries = 5;
    private const int DeadlockBackoffMilliseconds = 20;

    /// <summary>What one poll's claim attempts learned: jobs the exact check refused and flows at their step bound.</summary>
    private sealed class ClaimExclusions
    {
        public HashSet<Guid> Rejected { get; } = [];
        public HashSet<Guid> FullFlows { get; } = [];
        public bool Restart { get; set; }
    }

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
        ClaimSettings settings, long now, CancellationToken ct, Guid? only = null)
    {
        var parameters = new List<(string, object?)> { ("now", now), ("all", AllJobsLimitKey), ("queue", queue) };
        if (only is not null)
        {
            parameters.Add(("only", only.Value.ToString()));
        }

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
              {(only is null ? "" : "AND j.id=@only")}
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
        var output = status == JobStatus.Succeeded && settlement.Result is { } result ? await StoredAsync(connection, transaction, result.Output, ct) : null;
        await ExecuteBatchAsync(connection, transaction, ct,
        [
            new("""
                UPDATE dj_jobs SET status=@status,output=@output,error=@error,available_at=@available,max_attempts=max_attempts+@granted,
                lease_token=NULL,lease_expires_at=NULL,worker_id=NULL WHERE id=@id
                """, ("status", status.ToString()), ("output", output),
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
        await RequireProtocolAsync(connection, transaction, ct);
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
        await RequireProtocolAsync(connection, transaction, ct);
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

    /// <summary>The database chose this transaction as a deadlock victim and rolled it back.</summary>
    protected virtual bool IsDeadlockVictim(DbException error) => false;

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
