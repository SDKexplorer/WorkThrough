using System.Data.Common;

namespace WorkThrough.Storage;

/// <summary>
/// Row locks that order settlements and unrestricted claims, which run outside the admission lock (ADR-020). Every transaction that
/// changes a flow's steps locks the flow row before any step row, and the event sequence row is taken last, at commit.
/// </summary>
public abstract partial class RelationalJobStore
{
    /// <summary>
    /// A transaction for settlements and unrestricted claims. Providers whose row locks and reads satisfy the flow-lock ordering
    /// override this to run them without the admission lock; the default keeps them serialized.
    /// </summary>
    protected virtual Task<DbTransaction> BeginRowLockedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        BeginWriteAsync(connection, cancellationToken);

    /// <summary>
    /// A SELECT that locks the matching rows until commit. With <paramref name="skipLocked"/>, rows locked by another transaction are
    /// left out instead of awaited. The default takes no row locks, which suffices while every write holds the admission lock.
    /// </summary>
    protected virtual string LockingSelectSql(string columns, string table, string predicate, bool skipLocked) =>
        $"SELECT {columns} FROM {table} WHERE {predicate}";

    /// <summary>
    /// Selects up to <paramref name="limit"/> jobs in order, locking each and skipping any that another transaction holds, so
    /// concurrent claims receive disjoint jobs. The default takes no row locks, which suffices while every write holds the admission lock.
    /// </summary>
    protected virtual string SkipLockedJobsSql(string predicate, string order, int limit) =>
        $"SELECT * FROM dj_jobs j WHERE {predicate} ORDER BY {order} {PageSql(limit)}";

    private Task<T> RowLockedWriteAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action, CancellationToken ct) =>
        TransactAsync(BeginRowLockedWriteAsync, action, ct);

    /// <summary>Locks a flow before its steps change. A sweep passes <paramref name="skipLocked"/> and leaves a busy flow to a later sweep.</summary>
    private async Task<bool> LockFlowAsync(DbConnection connection, DbTransaction transaction, Guid runId, bool skipLocked, CancellationToken ct) =>
        await ScalarAsync(connection, transaction, LockingSelectSql("id", "dj_flows", "id=@id", skipLocked), ct, ("id", runId.ToString())) is not null;

    /// <summary>Locks the flows of the given jobs in one global order, so settlement batches cannot deadlock on each other.</summary>
    private async Task LockFlowsOfAsync(DbConnection connection, DbTransaction transaction, IEnumerable<Guid> jobIds, CancellationToken ct)
    {
        var parameters = jobIds.Distinct().Select((id, index) => ($"id{index}", (object?)id.ToString())).ToArray();
        var flows = new List<string>();
        await using (var command = Command(connection, transaction,
            $"SELECT DISTINCT workflow_run_id FROM dj_jobs WHERE workflow_run_id IS NOT NULL AND id IN ({string.Join(",", parameters.Select(p => "@" + p.Item1))})",
            parameters))
        {
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    flows.Add(reader.GetString(0));
                }
            }
        }

        foreach (var flow in flows.Order(StringComparer.Ordinal))
        {
            await LockFlowAsync(connection, transaction, Guid.Parse(flow), skipLocked: false, ct);
        }
    }

    /// <summary>Reads a job with its row locked, after locking its flow when it is a step.</summary>
    private async Task<JobRecord?> ReadJobForUpdateAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await LockFlowsOfAsync(connection, transaction, [id], ct);

        return (await ReadJobsAsync(connection, transaction, LockingSelectSql("*", "dj_jobs", "id=@id", skipLocked: false), ct,
            ("id", id.ToString()))).SingleOrDefault();
    }
}
