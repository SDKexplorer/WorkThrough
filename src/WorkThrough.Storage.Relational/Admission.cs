using System.Data.Common;

namespace WorkThrough.Storage;

/// <summary>Keyed concurrency, rate limits, flow parallelism and weighted queue fairness (ADR-018). Evaluated inside the admission transaction.</summary>
public abstract partial class RelationalJobStore : IAdmissionPolicies
{
    // A queue of weight w advances its pass by Stride / w per claim; weight 1 is plain round-robin.
    private const long Stride = 1_000_000;

    // Stored key_scope of rate limits that count all covered work together.
    private const int AllKeys = 0;

    /// <summary>The admission group of a job key: its alias target when processor aliases share one, otherwise itself.</summary>
    private static string AdmissionGroupSql(string jobKey) =>
        $"COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key={jobKey}),{jobKey})";

    private static string KeyColumn(KeyScope scope) => scope == KeyScope.Tenant ? "tenant_id" : "partition_key";

    /// <summary>True when a keyed limit of the scope covering the job has no free slot for its key. Requires @now and @all.</summary>
    private static string KeyBlockedSql(string job, KeyScope scope)
    {
        var column = KeyColumn(scope);

        return $"""
            ({job}.{column} IS NOT NULL AND EXISTS (
              SELECT 1 FROM dj_keyed_limits kl
              WHERE kl.key_scope={(int)scope} AND (kl.job_key=@all OR {AdmissionGroupSql("kl.job_key")}={AdmissionGroupSql($"{job}.job_key")})
                AND kl.maximum<=(
                  SELECT COUNT(*) FROM dj_jobs kr
                  WHERE kr.{column} IS NOT NULL AND kr.{column}={job}.{column} AND kr.status='Running' AND kr.lease_expires_at>@now
                    AND (kl.job_key=@all OR {AdmissionGroupSql("kr.job_key")}={AdmissionGroupSql("kl.job_key")}))))
            """;
    }

    /// <summary>A rate limit <c>rl</c> covers the job: its type scope matches and, for per-key limits, the job has that key.</summary>
    private static string RateAppliesSql(string job) => $"""
        (rl.key_scope={AllKeys} OR (rl.key_scope={(int)KeyScope.Partition} AND {job}.partition_key IS NOT NULL)
            OR (rl.key_scope={(int)KeyScope.Tenant} AND {job}.tenant_id IS NOT NULL))
          AND (rl.job_key=@all OR {AdmissionGroupSql("rl.job_key")}={AdmissionGroupSql($"{job}.job_key")})
        """;

    /// <summary>The ledger key the job counts under for rate limit <c>rl</c>. Requires @all.</summary>
    private static string RateKeySql(string job) =>
        $"CASE rl.key_scope WHEN {(int)KeyScope.Partition} THEN {job}.partition_key WHEN {(int)KeyScope.Tenant} THEN {job}.tenant_id ELSE @all END";

    /// <summary>True when a rate limit covering the job used every permit in its sliding window. Requires @now and @all.</summary>
    /// <remarks>Counts the rate ledger, an index range of one policy and key, so the cost stays bounded by the permits while a limit
    /// binds instead of growing with every start in the window.</remarks>
    private static string RateBlockedSql(string job) => $"""
        EXISTS (
          SELECT 1 FROM dj_rate_limits rl
          WHERE {RateAppliesSql(job)}
            AND rl.permits<=(
              SELECT COUNT(*) FROM dj_rate_ledger rd
              WHERE rd.job_key=rl.job_key AND rd.key_scope=rl.key_scope AND rd.rate_key={RateKeySql(job)} AND rd.started_at>@now-rl.window_ms))
        """;

    /// <summary>Records an admitted start against every rate limit covering it.</summary>
    private Task RecordRateStartAsync(DbConnection connection, DbTransaction transaction, Guid jobId, long now, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, $"""
            INSERT INTO dj_rate_ledger(job_key,key_scope,rate_key,started_at,job_id)
            SELECT rl.job_key,rl.key_scope,{RateKeySql("j")},@now,j.id FROM dj_rate_limits rl, dj_jobs j
            WHERE j.id=@id AND {RateAppliesSql("j")}
            """, ct, ("id", jobId.ToString()), ("now", now), ("all", AllJobsLimitKey));

    /// <summary>
    /// Drops starts older than any window can count. It runs in sweeps under the admission lock: concurrent claims deleting the same
    /// rows could deadlock.
    /// </summary>
    private Task PruneRateLedgerAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, "DELETE FROM dj_rate_ledger WHERE started_at<=@expired", ct, ("expired", RateLedgerExpiry(now)));

    private static long RateLedgerExpiry(long now) => now - (long)RateLimit.MaximumWindow.TotalMilliseconds;

    /// <summary>True when a per-type limit covering the job has no free slot. Requires @now and @all.</summary>
    private static string TypeBlockedSql(string job) => $"""
        EXISTS (
          SELECT 1 FROM dj_limits tl
          WHERE tl.job_key<>@all AND {AdmissionGroupSql("tl.job_key")}={AdmissionGroupSql($"{job}.job_key")}
            AND tl.maximum<=(
              SELECT COUNT(*) FROM dj_jobs tr
              WHERE tr.status='Running' AND tr.lease_expires_at>@now AND {AdmissionGroupSql("tr.job_key")}={AdmissionGroupSql($"{job}.job_key")}))
        """;

    /// <summary>True when the job's flow run already holds its maximum number of admitted step leases. Requires @now.</summary>
    private static string FlowBoundedSql(string job) => $"""
        ({job}.workflow_run_id IS NOT NULL AND EXISTS (
          SELECT 1 FROM dj_flows bf
          WHERE bf.id={job}.workflow_run_id AND bf.max_parallel IS NOT NULL AND bf.max_parallel<=(
            SELECT COUNT(*) FROM dj_jobs br WHERE br.workflow_run_id=bf.id AND br.status='Running' AND br.lease_expires_at>@now)))
        """;

    /// <summary>Whether the flow of job <paramref name="jobId"/> holds its maximum number of admitted step leases, read under its flow lock row.</summary>
    private async Task<bool> FlowAtBoundAsync(DbConnection connection, DbTransaction transaction, Guid jobId, long now, CancellationToken ct) =>
        Convert.ToInt32(await ScalarAsync(connection, transaction, $"SELECT COUNT(*) FROM dj_jobs j WHERE j.id=@id AND {FlowBoundedSql("j")}", ct,
            ("id", jobId.ToString()), ("now", now))) > 0;

    /// <summary>Claim predicates for the policy kinds that exist; absent kinds add nothing to the query.</summary>
    private static string AdmissionPredicatesSql(string job, ClaimSettings settings)
    {
        var predicates = new List<string>();
        if (settings.HasPartitionLimits)
        {
            predicates.Add(KeyBlockedSql(job, KeyScope.Partition));
        }

        if (settings.HasTenantLimits)
        {
            predicates.Add(KeyBlockedSql(job, KeyScope.Tenant));
        }

        if (settings.HasRateLimits)
        {
            predicates.Add(RateBlockedSql(job));
        }

        if (settings.HasBoundedFlows)
        {
            predicates.Add(FlowBoundedSql(job));
        }

        return string.Concat(predicates.Select(predicate => $"\n  AND NOT {predicate}"));
    }

    /// <summary>The worker's queues with ready work, lowest fair-share pass first, and whether several of them compete.</summary>
    private async Task<FairQueueOrder> FairQueueOrderAsync(DbConnection connection, DbTransaction transaction, ClaimRequest request, long now, CancellationToken ct)
    {
        if (request.Queues.Count == 1)
        {
            return new([.. request.Queues], Competing: false, Clock: 0);
        }

        var parameters = new List<(string, object?)> { ("now", now) };
        var queues = request.Queues.Select((queue, index) =>
        {
            parameters.Add(($"queue{index}", queue));

            return $"@queue{index}";
        }).ToArray();
        var ready = new List<(string Queue, long? Pass)>();
        await using (var command = Command(connection, transaction, $"""
            SELECT j.queue,MIN(t.pass) FROM dj_jobs j LEFT JOIN dj_queue_turns t ON t.queue=j.queue
            WHERE j.status='Queued' AND j.available_at<=@now AND j.queue IN ({string.Join(",", queues)})
            GROUP BY j.queue
            """, parameters.ToArray()))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ready.Add((reader.GetString(0), reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1))));
            }
        }

        if (ready.Count < 2)
        {
            return new(ready.Select(x => x.Queue).ToArray(), Competing: false, Clock: 0);
        }

        var clock = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT value FROM dj_queue_clock WHERE id=1", ct));
        var ordered = ready.OrderBy(x => Math.Max(x.Pass ?? clock, clock)).ThenBy(x => x.Queue, StringComparer.Ordinal).Select(x => x.Queue).ToArray();

        return new(ordered, Competing: true, clock);
    }

    /// <summary>Charges one claim to a competing queue. A queue never resumes below the virtual clock, so idleness earns no burst.</summary>
    private async Task AdvanceQueueTurnAsync(DbConnection connection, DbTransaction transaction, string queue, long clock, CancellationToken ct)
    {
        var stored = await ScalarAsync(connection, transaction, "SELECT pass FROM dj_queue_turns WHERE queue=@queue", ct, ("queue", queue));
        var weight = await ScalarAsync(connection, transaction, "SELECT weight FROM dj_queue_controls WHERE queue=@queue", ct, ("queue", queue));
        var start = Math.Max(stored is null or DBNull ? clock : Convert.ToInt64(stored), clock);
        var pass = start + Stride / (weight is null or DBNull ? QueueControl.DefaultWeight : Convert.ToInt32(weight));
        await ExecuteAsync(connection, transaction, UpsertSql("dj_queue_turns", "queue,pass", "@queue,@pass", "queue", "pass"), ct, ("queue", queue), ("pass", pass));
        await ExecuteAsync(connection, transaction, "UPDATE dj_queue_clock SET value=@start WHERE id=1 AND value<@start", ct, ("start", start));
    }

    private sealed record FairQueueOrder(IReadOnlyList<string> Queues, bool Competing, long Clock);

    /// <summary>
    /// A snapshot read, outside the admission lock, of whether a claim has anything to do: a sweep is due, or ready work exists in
    /// the requested queues while fleet capacity remains. Idle or saturated workers then poll without serializing other writers.
    /// </summary>
    /// <remarks>Only an optimization: a stale answer delays work by one poll, and admission is still decided under the lock.</remarks>
    private Task<ClaimOutlook> ClaimOutlookAsync(ClaimRequest request, CancellationToken cancellationToken) =>
        ReadAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            var parameters = new List<(string, object?)> { ("now", now), ("all", AllJobsLimitKey), ("expired", RateLedgerExpiry(now)) };
            var queues = request.Queues.Select((queue, index) =>
            {
                parameters.Add(($"queue{index}", queue));

                return $"@queue{index}";
            }).ToArray();
            await using var command = Command(connection, transaction, $"""
                SELECT
                  CASE WHEN EXISTS (SELECT 1 FROM dj_jobs WHERE status='Running' AND lease_expires_at<=@now)
                    OR EXISTS (SELECT 1 FROM dj_jobs WHERE status='Queued' AND expires_at<=@now)
                    OR EXISTS (SELECT 1 FROM dj_rate_ledger WHERE started_at<=@expired)
                    OR EXISTS (SELECT 1 FROM dj_signal_waits w WHERE w.deadline<=@now
                      OR EXISTS (SELECT 1 FROM dj_signals s WHERE s.run_id=w.run_id AND s.name=w.name))
                  THEN 1 ELSE 0 END,
                  CASE WHEN EXISTS (SELECT 1 FROM dj_jobs WHERE status='Queued' AND available_at<=@now AND queue IN ({string.Join(",", queues)}))
                  THEN 1 ELSE 0 END,
                  (SELECT maximum FROM dj_limits WHERE job_key=@all),
                  (SELECT COUNT(*) FROM dj_jobs WHERE status='Running' AND lease_expires_at>@now),
                  CASE WHEN EXISTS (SELECT 1 FROM dj_limits) OR EXISTS (SELECT 1 FROM dj_keyed_limits) OR EXISTS (SELECT 1 FROM dj_rate_limits)
                    OR EXISTS (SELECT 1 FROM dj_flows WHERE max_parallel IS NOT NULL AND status='Running')
                  THEN 0 ELSE 1 END,
                  (SELECT protocol FROM dj_compatibility WHERE id=1)
                FROM dj_registry r WHERE r.id=1
                """, parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var sweepDue = Convert.ToInt32(reader.GetValue(0)) != 0;
            var ready = Convert.ToInt32(reader.GetValue(1)) != 0;
            var fleetFull = !reader.IsDBNull(2) && Convert.ToInt64(reader.GetValue(3)) >= Convert.ToInt64(reader.GetValue(2));

            return new ClaimOutlook(sweepDue || ready && !fleetFull, sweepDue, Unrestricted: Convert.ToInt32(reader.GetValue(4)) != 0,
                Protocol: Convert.ToInt32(reader.GetValue(5)));
        }, cancellationToken);

    private sealed record ClaimOutlook(bool Proceed, bool SweepDue, bool Unrestricted, int Protocol);

    /// <summary>
    /// Claim configuration and the database clock, read in one round trip at the start of the admission transaction, so every value is exact for this claim.
    /// Absent policy kinds are left out of the claim query: PostgreSQL re-plans every claim and unused predicates doubled its planning time.
    /// </summary>
    private async Task<ClaimSettings> ReadClaimSettingsAsync(DbConnection connection, DbTransaction transaction, string workerId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT ({ClockSql}),r.revision,
              (SELECT COUNT(*) FROM dj_worker_controls WHERE worker_id=@worker AND draining=1),
              (SELECT maximum FROM dj_limits WHERE job_key=@all),
              (SELECT COUNT(*) FROM dj_limits WHERE job_key<>@all),
              (SELECT COUNT(*) FROM dj_keyed_limits WHERE key_scope={(int)KeyScope.Partition}),
              (SELECT COUNT(*) FROM dj_keyed_limits WHERE key_scope={(int)KeyScope.Tenant}),
              (SELECT COUNT(*) FROM dj_rate_limits),
              (SELECT COUNT(*) FROM dj_flows WHERE max_parallel IS NOT NULL AND status='Running'),
              (SELECT protocol FROM dj_compatibility WHERE id=1)
            FROM dj_registry r WHERE r.id=1
            """, ("worker", workerId), ("all", AllJobsLimitKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)) > 0,
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)), HasTypeLimits: Convert.ToInt64(reader.GetValue(4)) > 0,
            HasPartitionLimits: Convert.ToInt64(reader.GetValue(5)) > 0, HasTenantLimits: Convert.ToInt64(reader.GetValue(6)) > 0,
            HasRateLimits: Convert.ToInt64(reader.GetValue(7)) > 0, HasBoundedFlows: Convert.ToInt64(reader.GetValue(8)) > 0,
            Protocol: Convert.ToInt32(reader.GetValue(9)));
    }

    private sealed record ClaimSettings(long Now, long RegistryRevision, bool Draining, long? GlobalMaximum, bool HasTypeLimits, bool HasPartitionLimits,
        bool HasTenantLimits, bool HasRateLimits, bool HasBoundedFlows, int Protocol)
    {
        /// <summary>Refuses a claim this build cannot serve, before it leases anything.</summary>
        public void RequireClaimable(ClaimRequest request)
        {
            if (Protocol > EngineProtocol.Supported)
            {
                throw new EngineProtocolException(Protocol);
            }

            if (RegistryRevision != request.RegistryRevision)
            {
                throw new ProcessorRegistryChangedException();
            }
        }

        /// <summary>No limit, rate or bounded flow constrains admission, so claims need no shared view of running work.</summary>
        public bool Unrestricted => GlobalMaximum is null && !HasTypeLimits && !HasPartitionLimits && !HasTenantLimits && !HasRateLimits && !HasBoundedFlows;
    }

    public Task<AdmissionPolicies> AdmissionPoliciesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(async (connection, transaction, ct) =>
        {
            var limits = new List<ConcurrencyLimit>();
            await using (var command = Command(connection, transaction, "SELECT job_key,maximum FROM dj_limits ORDER BY job_key"))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    limits.Add(new(PolicyKey(reader.GetString(0)), Convert.ToInt32(reader.GetValue(1))));
                }
            }

            var keyed = new List<KeyedLimit>();
            await using (var command = Command(connection, transaction, "SELECT key_scope,job_key,maximum FROM dj_keyed_limits ORDER BY key_scope,job_key"))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    keyed.Add(new((KeyScope)Convert.ToInt32(reader.GetValue(0)), PolicyKey(reader.GetString(1)), Convert.ToInt32(reader.GetValue(2))));
                }
            }

            var rates = new List<RateLimit>();
            await using (var command = Command(connection, transaction, "SELECT job_key,key_scope,permits,window_ms FROM dj_rate_limits ORDER BY job_key,key_scope"))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rates.Add(new(PolicyKey(reader.GetString(0)), Convert.ToInt32(reader.GetValue(2)),
                        TimeSpan.FromMilliseconds(Convert.ToInt64(reader.GetValue(3))), StoredScope(Convert.ToInt32(reader.GetValue(1)))));
                }
            }

            return new AdmissionPolicies(limits, keyed, rates);
        }, cancellationToken);

    private static string? PolicyKey(string stored) => stored == AllJobsLimitKey ? null : stored;

    private static KeyScope? StoredScope(int stored) => stored == AllKeys ? null : (KeyScope)stored;

    private static int StoredScope(KeyScope? scope) => scope is { } value ? (int)value : AllKeys;

    public Task SeedAdmissionPoliciesAsync(IReadOnlyList<KeyedLimit> keyedLimits, IReadOnlyList<RateLimit> rateLimits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyedLimits);
        ArgumentNullException.ThrowIfNull(rateLimits);
        foreach (var limit in keyedLimits)
        {
            limit.Validate();
        }

        foreach (var limit in rateLimits)
        {
            limit.Validate();
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            foreach (var limit in keyedLimits)
            {
                await SaveKeyedLimitAsync(connection, transaction, limit, replace: false, ct);
            }

            foreach (var limit in rateLimits)
            {
                await SaveRateLimitAsync(connection, transaction, limit, replace: false, ct);
            }

            return true;
        }, cancellationToken);
    }

    public Task<Result> SetKeyedLimitAsync(KeyedLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.SetKeyedLimit, PolicyTarget(limit.JobKey, limit.Scope), limit, async (connection, transaction, ct) =>
        {
            await SaveKeyedLimitAsync(connection, transaction, limit, replace: true, ct);
        }, cancellationToken, validate: limit.Validate);

    public Task<Result> RemoveKeyedLimitAsync(KeyScope scope, string? jobKey, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.RemoveKeyedLimit, PolicyTarget(jobKey, scope), new { scope, jobKey }, async (connection, transaction, ct) =>
        {
            if (await ExecuteAsync(connection, transaction, "DELETE FROM dj_keyed_limits WHERE job_key=@key AND key_scope=@scope", ct,
                ("key", jobKey ?? AllJobsLimitKey), ("scope", (int)scope)) == 0)
            {
                throw new MissingStateException("No keyed limit exists for this scope and job key.");
            }
        }, cancellationToken, validate: () => ValidatePolicyScope(scope, jobKey));

    public Task<Result> SetRateLimitAsync(RateLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.SetRateLimit, PolicyTarget(limit.JobKey, limit.PerKey), limit, async (connection, transaction, ct) =>
        {
            await SaveRateLimitAsync(connection, transaction, limit, replace: true, ct);
        }, cancellationToken, validate: limit.Validate);

    public Task<Result> RemoveRateLimitAsync(string? jobKey, KeyScope? perKey, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.RemoveRateLimit, PolicyTarget(jobKey, perKey), new { jobKey, perKey }, async (connection, transaction, ct) =>
        {
            if (await ExecuteAsync(connection, transaction, "DELETE FROM dj_rate_limits WHERE job_key=@key AND key_scope=@scope", ct,
                ("key", jobKey ?? AllJobsLimitKey), ("scope", StoredScope(perKey))) == 0)
            {
                throw new MissingStateException("No rate limit exists for this job key and scope.");
            }

            await ExecuteAsync(connection, transaction, "DELETE FROM dj_rate_ledger WHERE job_key=@key AND key_scope=@scope", ct,
                ("key", jobKey ?? AllJobsLimitKey), ("scope", StoredScope(perKey)));
        }, cancellationToken, validate: () => ValidatePolicyScope(perKey, jobKey));

    private static string PolicyTarget(string? jobKey, KeyScope? scope) =>
        (jobKey ?? AllJobsLimitKey) + (scope is { } value ? "/per-" + value.ToString().ToLowerInvariant() : "");

    private static void ValidatePolicyScope(KeyScope? scope, string? jobKey)
    {
        if (scope is { } value)
        {
            AdmissionKeys.ValidateScope(value, nameof(scope));
        }

        AdmissionKeys.ValidateJobKey(jobKey);
    }

    private Task<int> SaveKeyedLimitAsync(DbConnection connection, DbTransaction transaction, KeyedLimit limit, bool replace, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, UpsertSql("dj_keyed_limits", "job_key,key_scope,maximum", "@key,@scope,@maximum", "job_key,key_scope",
            replace ? "maximum" : ""), ct,
            ("key", limit.JobKey ?? AllJobsLimitKey), ("scope", (int)limit.Scope), ("maximum", limit.MaximumPerKey));

    private Task<int> SaveRateLimitAsync(DbConnection connection, DbTransaction transaction, RateLimit limit, bool replace, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, UpsertSql("dj_rate_limits", "job_key,key_scope,permits,window_ms", "@key,@scope,@permits,@window",
            "job_key,key_scope", replace ? "permits,window_ms" : ""), ct,
            ("key", limit.JobKey ?? AllJobsLimitKey), ("scope", StoredScope(limit.PerKey)), ("permits", limit.Permits),
            ("window", (long)limit.Window.TotalMilliseconds));
}
