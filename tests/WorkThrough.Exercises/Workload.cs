using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;

namespace WorkThrough.Exercises;

internal static class Workload
{
    public const string Queue = "exercise";
    public static JobDefinition<WorkItem> Fast { get; } = new("exercises.fast");
    public static JobDefinition<WorkItem> Limited { get; } = new("exercises.limited");
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    public static async Task RunWorkerAsync()
    {
        var provider = Environment.GetEnvironmentVariable("WT_EXERCISE_PROVIDER")!;
        var connection = Environment.GetEnvironmentVariable("WT_EXERCISE_CONNECTION")!;
        var effects = Environment.GetEnvironmentVariable("WT_EXERCISE_EFFECTS")!;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new EffectStore(effects));
        var jobs = builder.Services.AddWorkThrough();
        if (provider == "sqlserver")
        {
            jobs.UseSqlServer(connection);
        }
        else if (provider == "postgres")
        {
            jobs.UsePostgreSql(connection);
        }
        else
        {
            jobs.UseSqlite(connection);
        }

        jobs.InitializeStorageOnStart(false)
            .AddProcessor<WorkItem, Processor>(Fast).AddProcessor<WorkItem, LimitedProcessor>(Limited)
            .AddWorker(options =>
            {
                options.WorkerId = $"exercise-{Environment.ProcessId}";
                options.Queues = [Queue];
                options.MaxConcurrentJobs = 4;
                options.PerJobLimits[Limited.Identity.Key] = 2;
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = Environment.GetEnvironmentVariable("WT_EXERCISE_CRASH") == "1" ? TimeSpan.FromSeconds(2) : Lease;
            });
        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine("READY");
        // Parent owns lifetime; killing this child is an intentional failure test.
        await host.WaitForShutdownAsync();
    }

    public sealed class Processor(EffectStore effects) : IProcessor<WorkItem>
    {
        public async Task<JobResult> ProcessAsync(WorkItem input, JobExecutionContext context, CancellationToken ct)
        {
            if (input.DelayMs > 0)
            {
                await Task.Delay(input.DelayMs, ct);
            }

            if (input.CrashAfterEffect)
            {
                await effects.ApplyAsync(context.IdempotencyKey, ct);
                if (context.Attempt == 1)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            }

            return JobResult.From(input.Index);
        }
    }
    public sealed class LimitedProcessor(EffectStore effects) : IProcessor<WorkItem>
    {
        private readonly Processor inner = new(effects);
        public Task<JobResult> ProcessAsync(WorkItem input, JobExecutionContext context, CancellationToken ct) => inner.ProcessAsync(input, context, ct);
    }
}

internal sealed record WorkItem(int Index, int DelayMs = 10, bool CrashAfterEffect = false);

// A separate business database models an idempotent external effect, outside the engine transaction.
internal sealed class EffectStore(string path)
{
    public async Task ApplyAsync(string key, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS effects(id TEXT PRIMARY KEY); INSERT INTO effects(id) VALUES (@id) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("@id", key);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<long> CountAsync()
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM effects";
        try
        {
            return (long)(await command.ExecuteScalarAsync())!;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 1)
        {
            return 0;
        }
    }
}
