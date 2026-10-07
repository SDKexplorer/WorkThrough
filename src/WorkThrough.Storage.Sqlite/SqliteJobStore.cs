using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough.Storage;

/// <summary>File-backed SQLite provider. Admission writes serialize through an immediate transaction.</summary>
public sealed class SqliteJobStore : RelationalJobStore
{
    private readonly string connectionString;

    public SqliteJobStore(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:" || string.IsNullOrWhiteSpace(builder.DataSource))
            throw new ArgumentException("Durable storage requires a file-backed SQLite database.", nameof(connectionString));
        this.connectionString = builder.ToString();
    }

    protected override DbConnection CreateConnection() => new SqliteConnection(connectionString);
    protected override string ClockSql => "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";

    protected override Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Microsoft.Data.Sqlite's async methods also execute synchronously; this acquires the write lock before reading admission state.
        return Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: false));
    }
}

public static class SqliteRegistration
{
    public static WorkThroughBuilder UseSqlite(this WorkThroughBuilder builder, string connectionString)
    {
        builder.Services.AddSingleton(new SqliteJobStore(connectionString));
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobQueries>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobAdministration>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IWorkflowDefinitions>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobDiagnostics>(sp => sp.GetRequiredService<SqliteJobStore>());
        builder.Services.AddSingleton<IJobSchedules>(sp => sp.GetRequiredService<SqliteJobStore>());
        return builder;
    }
}
