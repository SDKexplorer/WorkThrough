using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace WorkThrough.Storage;

/// <summary>PostgreSQL provider with a transaction-scoped admission lock for this table set.</summary>
public sealed class PostgresJobStore(string connectionString) : RelationalJobStore
{
    protected override DbConnection CreateConnection() => new NpgsqlConnection(connectionString);
    protected override string ClockSql => "SELECT CAST(FLOOR(EXTRACT(EPOCH FROM clock_timestamp()) * 1000) AS BIGINT)";

    protected override async Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);

    protected override async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // One short write boundary per store. Handlers never run while this lock is held.
            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(68491730251)", cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}

public static class PostgresRegistration
{
    public static WorkThroughBuilder UsePostgreSql(this WorkThroughBuilder builder, string connectionString)
    {
        builder.Services.AddSingleton(new PostgresJobStore(connectionString));
        builder.Services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobQueries>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IExecutionQueries>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobAdministration>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IWorkflowDefinitions>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobDiagnostics>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobSchedules>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IDeploymentQueries>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IProcessorRegistry>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IExecutionOperations>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IExecutionArchives>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IWorkflowSignals>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IJobOutbox>(sp => sp.GetRequiredService<PostgresJobStore>());
        builder.Services.AddSingleton<IExecutionEvents>(sp => sp.GetRequiredService<PostgresJobStore>());

        return builder;
    }
}
