using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace WorkThrough.Storage;

/// <summary>File-backed SQLite provider. Admission writes serialize through an immediate transaction.</summary>
public sealed record SqliteStorageOptions(bool UseWriteAheadLogging = true);

public sealed class SqliteJobStore : RelationalJobStore
{
    protected override string ReadinessWorkersSql => """
        WITH recent AS (
          SELECT id,seen_at,CASE WHEN LENGTH(details)<=262144 THEN details ELSE '{}' END AS info
          FROM dj_workers WHERE seen_at>=@fresh ORDER BY seen_at DESC,id LIMIT 501)
        SELECT SUBSTR(r.id,1,512),r.seen_at,COALESCE(json_extract(r.info,'$.capacity'),0),
          COALESCE(json_extract(r.info,'$.running'),0),COALESCE(c.draining,0),
          CASE WHEN EXISTS(SELECT 1 FROM json_each(r.info,'$.processors') p WHERE
            json_extract(p.value,'$.key')=j.job_key AND json_extract(p.value,'$.version')=j.contract_version)
            AND EXISTS(SELECT 1 FROM json_each(r.info,'$.queues') q WHERE q.value=j.queue) THEN 1 ELSE 0 END,
          CASE WHEN json_type(r.info,'$.processors')='array' AND json_type(r.info,'$.queues')='array'
            AND json_type(r.info,'$.capacity')='integer' AND json_type(r.info,'$.running')='integer' THEN 1 ELSE 0 END,
          CASE WHEN LENGTH(r.id)>512 THEN 1 ELSE 0 END,json_extract(r.info,'$.acquisition.consecutiveFailures')
        FROM recent r JOIN dj_jobs j ON j.id=@id LEFT JOIN dj_worker_controls c ON c.worker_id=r.id
        ORDER BY r.seen_at DESC,r.id
        """;
    protected override string AuditActorValueSql(string field) =>
        $"json_extract(CASE WHEN LENGTH(a.actor)<=16384 THEN a.actor ELSE '{{}}' END,'$.{field}')";
    private readonly string connectionString;
    private readonly bool useWriteAheadLogging;
    private readonly int busyTimeoutMilliseconds;

    public SqliteJobStore(string connectionString) : this(connectionString, new SqliteStorageOptions())
    {
    }

    public SqliteJobStore(string connectionString, SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        useWriteAheadLogging = options.UseWriteAheadLogging;
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:" || string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new ArgumentException("Durable storage requires a file-backed SQLite database.", nameof(connectionString));
        }

        busyTimeoutMilliseconds = checked(builder.DefaultTimeout * 1000);
        this.connectionString = builder.ToString();
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(cancellationToken);
        if (!useWriteAheadLogging)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        if (!string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken), "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite could not enable WAL. Use a supported local filesystem or explicitly preserve the database journal mode.");
        }
    }

    protected override async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await base.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            // SQLite's own busy handler waits in small steps for the single write lock; without it the driver retries a busy
            // statement after long fixed sleeps, which collapses throughput as soon as processes contend.
            command.CommandText = $"PRAGMA synchronous=FULL; PRAGMA busy_timeout={busyTimeoutMilliseconds}";
            await command.ExecuteNonQueryAsync(ct);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    protected override DbConnection CreateConnection() => new SqliteConnection(connectionString);
    protected override string ClockSql => "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";
    protected override string FlowDependencySql => """
        SELECT DISTINCT predecessor_job.id,successor_job.id
        FROM dj_flows f JOIN dj_definitions d ON d.flow_key=f.flow_key AND d.version=f.version
        JOIN json_each(CASE WHEN LENGTH(d.definition)<=@maximum THEN d.definition ELSE '{"steps":[]}' END,'$.steps') step
        JOIN json_each(step.value,'$.after') dependency
        JOIN dj_jobs successor_job ON successor_job.workflow_run_id=f.id AND successor_job.step_id=json_extract(step.value,'$.id')
        JOIN dj_jobs predecessor_job ON predecessor_job.workflow_run_id=f.id AND predecessor_job.step_id=dependency.value
        WHERE f.id=@run ORDER BY successor_job.id,predecessor_job.id LIMIT @limit OFFSET @offset
        """;

    protected override Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: true));
    }

    protected override Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Microsoft.Data.Sqlite's async methods also execute synchronously; this acquires the write lock before reading admission state.
        return Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: false));
    }
}
