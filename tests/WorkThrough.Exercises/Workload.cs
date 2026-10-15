using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;

namespace WorkThrough.Exercises;

internal static class Workload
{
    public const string Queue = "exercise";
    public const int LocalSlots = 4;
    public const int PollMilliseconds = 10;
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
        if (provider == "oracle")
        {
            jobs.UseOracle(connection);
        }
        else if (provider == "sqlserver")
        {
            jobs.UseSqlServer(connection);
        }
        else if (provider == "postgres")
        {
            jobs.UsePostgreSql(connection);
        }
        else
        {
            jobs.UseMySql(connection);
        }

        jobs.InitializeStorageOnStart(false)
            .AddJob<WorkItem, Processor>(Fast).AddJob<WorkItem, LimitedProcessor>(Limited)
            .AddWorker(options =>
            {
                // Containers all run as process 1, so they name themselves.
                options.WorkerId = Environment.GetEnvironmentVariable("WT_EXERCISE_WORKER_ID") is { Length: > 0 } id ? id : $"exercise-{Environment.ProcessId}";
                options.Queues = Environment.GetEnvironmentVariable("WT_EXERCISE_QUEUES") is { Length: > 0 } queues ? queues.Split(',') : [Queue];
                options.MaxConcurrentJobs = int.TryParse(Environment.GetEnvironmentVariable("WT_EXERCISE_SLOTS"), out var slots) ? slots : LocalSlots;
                options.PerJobLimits[Limited.Identity.Key] = 2;
                options.PollInterval = TimeSpan.FromMilliseconds(PollMilliseconds);
                options.LeaseDuration = Environment.GetEnvironmentVariable("WT_EXERCISE_CRASH") == "1" ? TimeSpan.FromSeconds(2) : Lease;
            });
        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine("READY");
        // Parent owns lifetime; killing this child is an intentional failure test.
        await host.WaitForShutdownAsync();
    }

    public sealed class Processor(EffectStore effects) : IJob<WorkItem>
    {
        public async Task<JobResult> ExecuteAsync(WorkItem input, JobExecutionContext context, CancellationToken ct)
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
    public sealed class LimitedProcessor(EffectStore effects) : IJob<WorkItem>
    {
        private readonly Processor inner = new(effects);
        public Task<JobResult> ExecuteAsync(WorkItem input, JobExecutionContext context, CancellationToken ct) => inner.ExecuteAsync(input, context, ct);
    }
}

internal sealed record WorkItem(int Index, int DelayMs = 10, bool CrashAfterEffect = false);

// A separate business system models an idempotent external effect, outside the engine transaction: one marker file per effect key.
internal sealed class EffectStore(string path)
{
    public Task ApplyAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        try
        {
            using var _ = new FileStream(Path.Combine(path, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(key))), FileMode.CreateNew);
        }
        catch (IOException) when (File.Exists(Path.Combine(path, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(key)))))
        {
            // Applied before: the effect is idempotent.
        }

        return Task.CompletedTask;
    }

    public Task<long> CountAsync() => Task.FromResult(Directory.Exists(path) ? (long)Directory.GetFiles(path).Length : 0);
}
