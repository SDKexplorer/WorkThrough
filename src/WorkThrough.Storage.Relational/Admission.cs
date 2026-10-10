using System.Data.Common;

namespace WorkThrough.Storage;

/// <summary>Partition concurrency, rate limits and weighted queue fairness (ADR-018). Evaluated inside the admission transaction.</summary>
public abstract partial class RelationalJobStore : IAdmissionPolicies
{
    // A queue of weight w advances its pass by Stride / w per claim; weight 1 is plain round-robin.
    private const long Stride = 1_000_000;

    /// <summary>The admission group of a job key: its alias target when processor aliases share one, otherwise itself.</summary>
    private static string AdmissionGroupSql(string jobKey) =>
        $"COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key={jobKey}),{jobKey})";

    /// <summary>True when a partition limit covering the job has no free slot for its key. Requires @now and @all.</summary>
    private static string PartitionBlockedSql(string job) => $"""
        ({job}.partition_key IS NOT NULL AND EXISTS (
          SELECT 1 FROM dj_partition_limits pl
          WHERE (pl.job_key=@all OR {AdmissionGroupSql("pl.job_key")}={AdmissionGroupSql($"{job}.job_key")})
            AND pl.maximum<=(
              SELECT COUNT(*) FROM dj_jobs pr
              WHERE pr.partition_key IS NOT NULL AND pr.partition_key={job}.partition_key AND pr.status='Running' AND pr.lease_expires_at>@now
                AND (pl.job_key=@all OR {AdmissionGroupSql("pr.job_key")}={AdmissionGroupSql("pl.job_key")}))))
        """;

    /// <summary>True when a rate limit covering the job used every permit in its sliding window. Requires @now and @all.</summary>
    private static string RateBlockedSql(string job) => $"""
        EXISTS (
          SELECT 1 FROM dj_rate_limits rl
          WHERE (rl.per_partition=0 OR {job}.partition_key IS NOT NULL)
            AND (rl.job_key=@all OR {AdmissionGroupSql("rl.job_key")}={AdmissionGroupSql($"{job}.job_key")})
            AND rl.permits<=(
              SELECT COUNT(*) FROM dj_attempts ra JOIN dj_jobs rj ON rj.id=ra.job_id
              WHERE ra.started_at>@now-rl.window_ms
                AND (rl.job_key=@all OR {AdmissionGroupSql("rj.job_key")}={AdmissionGroupSql("rl.job_key")})
                AND (rl.per_partition=0 OR rj.partition_key={job}.partition_key)))
        """;

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
    /// Claim configuration read in one round trip at the start of the admission transaction, so every value is exact for this claim.
    /// Absent policy kinds are left out of the claim query: PostgreSQL re-plans every claim and unused predicates doubled its planning time.
    /// </summary>
    private async Task<ClaimSettings> ReadClaimSettingsAsync(DbConnection connection, DbTransaction transaction, string workerId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT r.revision,
              (SELECT COUNT(*) FROM dj_worker_controls WHERE worker_id=@worker AND draining=1),
              (SELECT maximum FROM dj_limits WHERE job_key=@all),
              (SELECT COUNT(*) FROM dj_partition_limits),
              (SELECT COUNT(*) FROM dj_rate_limits)
            FROM dj_registry r WHERE r.id=1
            """, ("worker", workerId), ("all", AllJobsLimitKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)) > 0,
            reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2)),
            Convert.ToInt64(reader.GetValue(3)) > 0, Convert.ToInt64(reader.GetValue(4)) > 0);
    }

    private sealed record ClaimSettings(long RegistryRevision, bool Draining, long? GlobalMaximum, bool HasPartitionLimits, bool HasRateLimits);

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

            var partitions = new List<PartitionLimit>();
            await using (var command = Command(connection, transaction, "SELECT job_key,maximum FROM dj_partition_limits ORDER BY job_key"))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    partitions.Add(new(PolicyKey(reader.GetString(0)), Convert.ToInt32(reader.GetValue(1))));
                }
            }

            var rates = new List<RateLimit>();
            await using (var command = Command(connection, transaction, "SELECT job_key,per_partition,permits,window_ms FROM dj_rate_limits ORDER BY job_key,per_partition"))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rates.Add(new(PolicyKey(reader.GetString(0)), Convert.ToInt32(reader.GetValue(2)),
                        TimeSpan.FromMilliseconds(Convert.ToInt64(reader.GetValue(3))), Convert.ToInt32(reader.GetValue(1)) != 0));
                }
            }

            return new AdmissionPolicies(limits, partitions, rates);
        }, cancellationToken);

    private static string? PolicyKey(string stored) => stored == AllJobsLimitKey ? null : stored;

    public Task SeedAdmissionPoliciesAsync(IReadOnlyList<PartitionLimit> partitionLimits, IReadOnlyList<RateLimit> rateLimits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partitionLimits);
        ArgumentNullException.ThrowIfNull(rateLimits);
        foreach (var limit in partitionLimits)
        {
            limit.Validate();
        }

        foreach (var limit in rateLimits)
        {
            limit.Validate();
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            foreach (var limit in partitionLimits)
            {
                await SavePartitionLimitAsync(connection, transaction, limit, replace: false, ct);
            }

            foreach (var limit in rateLimits)
            {
                await SaveRateLimitAsync(connection, transaction, limit, replace: false, ct);
            }

            return true;
        }, cancellationToken);
    }

    public Task<Result> SetPartitionLimitAsync(PartitionLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.SetPartitionLimit, limit.JobKey ?? AllJobsLimitKey, limit, async (connection, transaction, ct) =>
        {
            await SavePartitionLimitAsync(connection, transaction, limit, replace: true, ct);
        }, cancellationToken, validate: limit.Validate);

    public Task<Result> RemovePartitionLimitAsync(string? jobKey, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.RemovePartitionLimit, jobKey ?? AllJobsLimitKey, new { jobKey }, async (connection, transaction, ct) =>
        {
            if (await ExecuteAsync(connection, transaction, "DELETE FROM dj_partition_limits WHERE job_key=@key", ct, ("key", jobKey ?? AllJobsLimitKey)) == 0)
            {
                throw new MissingStateException("No partition limit exists for this scope.");
            }
        }, cancellationToken, validate: () => ValidatePolicyKey(jobKey));

    public Task<Result> SetRateLimitAsync(RateLimit limit, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.SetRateLimit, RateTarget(limit.JobKey, limit.PerPartition), limit, async (connection, transaction, ct) =>
        {
            await SaveRateLimitAsync(connection, transaction, limit, replace: true, ct);
        }, cancellationToken, validate: limit.Validate);

    public Task<Result> RemoveRateLimitAsync(string? jobKey, bool perPartition, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.RemoveRateLimit, RateTarget(jobKey, perPartition), new { jobKey, perPartition }, async (connection, transaction, ct) =>
        {
            if (await ExecuteAsync(connection, transaction, "DELETE FROM dj_rate_limits WHERE job_key=@key AND per_partition=@partitioned", ct,
                ("key", jobKey ?? AllJobsLimitKey), ("partitioned", perPartition ? 1 : 0)) == 0)
            {
                throw new MissingStateException("No rate limit exists for this scope.");
            }
        }, cancellationToken, validate: () => ValidatePolicyKey(jobKey));

    private static string RateTarget(string? jobKey, bool perPartition) => (jobKey ?? AllJobsLimitKey) + (perPartition ? "/per-partition" : "");

    private static void ValidatePolicyKey(string? jobKey)
    {
        if (jobKey is not null)
        {
            _ = new JobContractId(jobKey, 1);
        }
    }

    private Task<int> SavePartitionLimitAsync(DbConnection connection, DbTransaction transaction, PartitionLimit limit, bool replace, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, UpsertSql("dj_partition_limits", "job_key,maximum", "@key,@maximum", "job_key", replace ? "maximum" : ""), ct,
            ("key", limit.JobKey ?? AllJobsLimitKey), ("maximum", limit.MaximumPerPartition));

    private Task<int> SaveRateLimitAsync(DbConnection connection, DbTransaction transaction, RateLimit limit, bool replace, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, UpsertSql("dj_rate_limits", "job_key,per_partition,permits,window_ms", "@key,@partitioned,@permits,@window",
            "job_key,per_partition", replace ? "permits,window_ms" : ""), ct,
            ("key", limit.JobKey ?? AllJobsLimitKey), ("partitioned", limit.PerPartition ? 1 : 0), ("permits", limit.Permits),
            ("window", (long)limit.Window.TotalMilliseconds));
}
