using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough.Storage;

/// <summary>File-backed SQLite provider. Admission writes serialize through an immediate transaction.</summary>
public sealed record SqliteStorageOptions(bool UseWriteAheadLogging = true);

public sealed class SqliteJobStore : RelationalJobStore
{
    protected override string AuditActorValueSql(string field) =>
        $"json_extract(CASE WHEN LENGTH(a.actor)<=16384 THEN a.actor ELSE '{{}}' END,'$.{field}')";
    private readonly string connectionString;
    private readonly bool useWriteAheadLogging;

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
            command.CommandText = "PRAGMA synchronous=FULL";
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

public static class SqliteRegistration
{
    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString) =>
        UseSqlite(builder, connectionString, new SqliteStorageOptions());

    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString, SqliteStorageOptions options)
    {
        builder.Services.AddSingleton(new SqliteJobStore(connectionString, options));
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobQueries>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IExecutionQueries>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IExecutionInspectionQueries>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IOperationHistoryQueries>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobAdministration>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IWorkflowDefinitions>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobDiagnostics>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobSchedules>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IDeploymentQueries>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IProcessorRegistry>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IExecutionOperations>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IExecutionArchives>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IWorkflowSignals>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobOutbox>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IExecutionEvents>(sp => sp.GetRequiredService<SqliteJobStore>());

        return builder;
    }
}
