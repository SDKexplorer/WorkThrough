using System.Data.Common;
using Npgsql;

namespace WorkThrough.Storage;

/// <summary>PostgreSQL provider with a transaction-scoped admission lock.</summary>
/// <remarks>The advisory lock is database-wide: WorkThrough stores in different schemas of one database share it.
/// Its key is part of the cross-version admission contract and must not change.</remarks>
public sealed class PostgresJobStore(string connectionString) : RelationalJobStore
{
    protected override string ReadinessWorkersSql => """
        WITH recent AS (
          SELECT id,seen_at,(CASE WHEN LENGTH(details)<=262144 THEN details ELSE '{}' END)::jsonb AS info
          FROM dj_workers WHERE seen_at>=@fresh ORDER BY seen_at DESC,id LIMIT 501)
        SELECT SUBSTR(r.id,1,512),r.seen_at,COALESCE((r.info->>'capacity')::integer,0),
          COALESCE((r.info->>'running')::integer,0),COALESCE(c.draining,0),
          CASE WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(r.info->'processors') p WHERE
            p->>'key'=j.job_key AND (p->>'version')::integer=j.contract_version)
            AND EXISTS(SELECT 1 FROM jsonb_array_elements_text(r.info->'queues') q(value) WHERE q.value=j.queue) THEN 1 ELSE 0 END,
          CASE WHEN jsonb_typeof(r.info->'processors')='array' AND jsonb_typeof(r.info->'queues')='array'
            AND jsonb_typeof(r.info->'capacity')='number' AND jsonb_typeof(r.info->'running')='number' THEN 1 ELSE 0 END,
          CASE WHEN LENGTH(r.id)>512 THEN 1 ELSE 0 END,(r.info->'acquisition'->>'consecutiveFailures')::integer
        FROM recent r JOIN dj_jobs j ON j.id=@id LEFT JOIN dj_worker_controls c ON c.worker_id=r.id
        ORDER BY r.seen_at DESC,r.id
        """;
    protected override string AuditActorValueSql(string field) =>
        $"((CASE WHEN LENGTH(a.actor)<=16384 THEN a.actor ELSE '{{}}' END)::jsonb->>'{field}')";
    protected override DbConnection CreateConnection() => new NpgsqlConnection(connectionString);
    protected override string ColumnExistsSql =>
        "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table AND column_name=@column";
    protected override string ClockSql => "SELECT CAST(FLOOR(EXTRACT(EPOCH FROM clock_timestamp()) * 1000) AS BIGINT)";
    protected override string FlowDependencySql => """
        SELECT DISTINCT predecessor_job.id,successor_job.id
        FROM dj_flows f JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version
        CROSS JOIN LATERAL jsonb_array_elements((CASE WHEN LENGTH(d.definition)<=@maximum THEN d.definition ELSE '{"steps":[]}' END)::jsonb->'steps') step(value)
        CROSS JOIN LATERAL jsonb_array_elements_text(step.value->'after') dependency(value)
        JOIN dj_jobs successor_job ON successor_job.workflow_run_id=f.id AND successor_job.step_id=step.value->>'id'
        JOIN dj_jobs predecessor_job ON predecessor_job.workflow_run_id=f.id AND predecessor_job.step_id=dependency.value
        WHERE f.id=@run ORDER BY successor_job.id,predecessor_job.id LIMIT @limit OFFSET @offset
        """;

    protected override async Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            // Store reads are bounded and index-driven; JIT compilation would dominate their cost, as in admission.
            await ExecuteAsync(connection, transaction, "SET LOCAL jit = off", cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private const long AdmissionLockKey = 68491730251;

    /// <summary>Owned-row writes take no advisory lock; their lease token or worker ID fences them.</summary>
    protected override async Task<DbTransaction> BeginOwnedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(cancellationToken);

    /// <summary>Settlements and unrestricted claims take no advisory lock; flow and job row locks order them against other writers.</summary>
    protected override async Task<DbTransaction> BeginRowLockedWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(cancellationToken);

    protected override string LockingSelectSql(string columns, string table, string predicate, bool skipLocked) =>
        $"SELECT {columns} FROM {table} WHERE {predicate} FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}";

    protected override string SkipLockedJobsSql(string predicate, string order, int limit) =>
        base.SkipLockedJobsSql(predicate, order, limit) + " FOR UPDATE SKIP LOCKED";

    protected override async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // One short write boundary per database. Handlers never run while this lock is held.
            // Admission statements touch few rows, but their correlated policy checks have high estimated costs. JIT compilation
            // then costs hundreds of milliseconds per claim for no benefit, so it is disabled for this transaction only.
            await LockAdmissionAsync(connection, transaction, cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    protected override Task LockAdmissionAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(@key); SET LOCAL jit = off", cancellationToken, ("key", AdmissionLockKey));
}
