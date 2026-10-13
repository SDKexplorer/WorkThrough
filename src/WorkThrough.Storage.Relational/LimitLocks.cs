using System.Data.Common;
using System.Text;

namespace WorkThrough.Storage;

/// <summary>
/// Lock rows that let claims under shared limits run without the admission lock (ADR-022). Each limit and key hashes to one row;
/// a claim locks the rows of its candidates, then counts admitted leases exactly as before. Releasing capacity needs no lock.
/// </summary>
public abstract partial class RelationalJobStore
{
    /// <summary>Lock rows seeded by schema 7. Keys that share a row only serialize their claims; limits stay exact.</summary>
    protected const int LimitLockRows = 4096;

    /// <summary>The alias groups and rate policies needed to name a candidate's lock rows.</summary>
    private sealed record LimitPolicies(Dictionary<string, string> Groups, (string JobKey, int Scope)[] Rates)
    {
        public string Group(string jobKey) => Groups.GetValueOrDefault(jobKey, jobKey);
    }

    /// <summary>Inserts the missing lock rows 0 to <see cref="LimitLockRows"/>-1 in one statement.</summary>
    protected virtual string SeedLimitLocksSql => $"""
        WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<{LimitLockRows - 1})
        INSERT INTO dj_limit_locks(id) SELECT i FROM n WHERE NOT EXISTS (SELECT 1 FROM dj_limit_locks l WHERE l.id=n.i)
        """;

    protected async Task SeedLimitLocksAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_limit_locks", ct)) < LimitLockRows)
        {
            await ExecuteAsync(connection, transaction, SeedLimitLocksSql, ct);
        }
    }

    private async Task<LimitPolicies> ReadLimitPoliciesAsync(DbConnection connection, DbTransaction transaction, ClaimSettings settings, CancellationToken ct)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var rates = new List<(string, int)>();
        if (settings.HasTypeLimits || settings.HasRateLimits)
        {
            await using var command = Command(connection, transaction, "SELECT job_key,group_key FROM dj_processor_groups");
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                groups[reader.GetString(0)] = reader.GetString(1);
            }
        }

        if (settings.HasRateLimits)
        {
            await using var command = Command(connection, transaction, "SELECT job_key,key_scope FROM dj_rate_limits");
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rates.Add((reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));
            }
        }

        return new(groups, [.. rates]);
    }

    /// <summary>Every limit whose admission count the job would change, named so that equal limits and keys share a name.</summary>
    private static IEnumerable<string> LimitScopes(JobRecord job, ClaimSettings settings, LimitPolicies policies)
    {
        if (settings.GlobalMaximum is not null)
        {
            yield return "fleet";
        }

        if (settings.HasTypeLimits)
        {
            yield return $"type|{policies.Group(job.Contract.Key)}";
        }

        if (settings.HasPartitionLimits && job.PartitionKey is { } partition)
        {
            yield return $"partition|{partition}";
        }

        if (settings.HasTenantLimits && job.TenantId is { } tenant)
        {
            yield return $"tenant|{tenant}";
        }

        foreach (var (rateKey, scope) in policies.Rates)
        {
            var key = (KeyScope)scope switch
            {
                KeyScope.Partition => job.PartitionKey,
                KeyScope.Tenant => job.TenantId,
                _ => AllJobsLimitKey
            };
            if (key is not null && (rateKey == AllJobsLimitKey || policies.Group(rateKey) == policies.Group(job.Contract.Key)))
            {
                yield return $"rate|{rateKey}|{scope}|{key}";
            }
        }

        if (settings.HasBoundedFlows && job.WorkflowRunId is { } run)
        {
            yield return $"flow|{run}";
        }
    }

    /// <summary>The lock row of a limit scope: a stable FNV-1a hash, so every host maps a scope to the same row.</summary>
    private static int LimitLockRow(string scope)
    {
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes(scope))
        {
            hash = (hash ^ value) * 16777619u;
        }

        return (int)(hash % LimitLockRows);
    }

    /// <summary>
    /// Locks the rows in ascending order. A claim waits only while it holds no limit lock, so waits cannot form a cycle; otherwise a
    /// busy row returns false and the claim ends its batch.
    /// </summary>
    private async Task<bool> LockLimitRowsAsync(DbConnection connection, DbTransaction transaction, IEnumerable<int> rows, bool wait, CancellationToken ct)
    {
        var ids = rows.Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return true;
        }

        var locked = 0;
        await using var command = Command(connection, transaction,
            LockingSelectSql("id", "dj_limit_locks", $"id IN ({string.Join(",", ids)}) ORDER BY id", skipLocked: !wait));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            locked++;
        }

        return locked == ids.Length;
    }
}
