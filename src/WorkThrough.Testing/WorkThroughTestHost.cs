using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;

namespace WorkThrough.Testing;

/// <summary>
/// Runs WorkThrough as production does, on a throwaway SQLite file: the real store, worker and scheduler, with a clock tests can move.
/// </summary>
/// <example>
/// <code>
/// await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddJobs(typeof(ChargeJob).Assembly),
///     services => services.AddSingleton&lt;IPayments, FakePayments&gt;());
/// var run = await host.Flows.StartAsync(Checkout.Definition, order);
/// (await host.WaitForFlowAsync(run)).AssertSucceeded();
/// </code>
/// </example>
public sealed class WorkThroughTestHost : IAsyncDisposable
{
    /// <summary>How long a wait lasts before it fails with the current state.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private const int IdleScanLimit = 500;

    private readonly IHost host;
    private readonly string path;
    private readonly StoreClock clock;
    private readonly IReadOnlyList<string> queues;

    private WorkThroughTestHost(IHost host, string path, StoreClock clock, IReadOnlyList<string> queues)
    {
        this.host = host;
        this.path = path;
        this.clock = clock;
        this.queues = queues;
    }

    /// <summary>The offset the store adds to its clock; shared with the store when the host is built.</summary>
    private sealed class StoreClock
    {
        public TimeSpan Offset { get; set; }
    }

    public IServiceProvider Services => host.Services;
    public JobClient Jobs => Services.GetRequiredService<JobClient>();
    public WorkflowClient Flows => Services.GetRequiredService<WorkflowClient>();
    public IJobQueries Queries => Services.GetRequiredService<IJobQueries>();
    public RelationalJobStore Store => (RelationalJobStore)Services.GetRequiredService<IJobStore>();

    /// <summary>The store's current time: the real time plus every <see cref="AdvanceAsync"/>.</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + clock.Offset;

    /// <summary>
    /// Starts a host with jobs registered by <paramref name="configure"/>, plus a worker and scheduler polling every 10 ms.
    /// <paramref name="services"/> replaces dependencies, such as a payment client, with test doubles. The worker serves
    /// <paramref name="queues"/>, by default only the default queue.
    /// </summary>
    public static async Task<WorkThroughTestHost> StartAsync(Action<WorkThroughBuilder> configure, Action<IServiceCollection>? services = null,
        IReadOnlyList<string>? queues = null, CancellationToken cancellationToken = default)
    {
        queues ??= [JobDefaults.Queue];
        ArgumentNullException.ThrowIfNull(configure);
        var path = Path.Combine(Path.GetTempPath(), $"workthrough-test-{Guid.NewGuid():N}.db");
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var clock = new StoreClock();
        var workthrough = builder.Services.AddWorkThrough()
            .UseSqlite($"Data Source={path}", new SqliteStorageOptions { ClockOffset = () => clock.Offset });
        configure(workthrough);
        workthrough.AddScheduler().AddWorker(options =>
        {
            options.WorkerId = "test-worker";
            options.PollInterval = PollInterval;
            options.Queues = [.. queues];
        });
        services?.Invoke(builder.Services);
        var created = new WorkThroughTestHost(builder.Build(), path, clock, queues);
        await created.host.StartAsync(cancellationToken);

        return created;
    }

    /// <summary>
    /// Moves the store's clock forward, so delayed jobs, retries, signal deadlines and schedules fall due. Leases age too: advancing past
    /// a lease while a job runs makes it look abandoned, as a crashed worker would.
    /// </summary>
    public Task AdvanceAsync(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The clock only moves forward.");
        }

        clock.Offset += duration;

        return Task.CompletedTask;
    }

    /// <summary>Waits until the job is terminal and returns it.</summary>
    public async Task<JobRecord> WaitForJobAsync(Guid jobId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        JobRecord? job = null;
        await WaitAsync(async ct => (job = await Queries.GetAsync(jobId, ct))?.Status.IsTerminal() == true,
            () => $"Job {jobId} is {job?.Status.ToString() ?? "missing"}.", timeout, cancellationToken);

        return job!;
    }

    /// <summary>Waits until the run succeeds, fails or needs an operator, then returns it with every step.</summary>
    public async Task<FlowResult> WaitForFlowAsync(Guid runId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        FlowResult? result = null;
        await WaitAsync(async ct =>
        {
            result = await FlowResultAsync(runId, ct);

            return result.Run.Status is FlowStatus.Succeeded or FlowStatus.Failed or FlowStatus.Incident;
        }, () => result is null ? $"Flow {runId} does not exist." : result.Describe() + UnservedQueues(result.Jobs), timeout, cancellationToken);

        return result!;
    }

    /// <summary>The run and its steps now, without waiting.</summary>
    public async Task<FlowResult> FlowResultAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await Queries.GetWorkflowAsync(runId, cancellationToken) ?? throw new InvalidOperationException($"Flow {runId} does not exist.");

        return new(run, await Queries.ListFlowJobsAsync(runId, cancellationToken));
    }

    /// <summary>Waits until nothing runs and no queued job is due at the store's time.</summary>
    public Task WaitUntilIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JobRecord> busy = [];

        return WaitAsync(async ct =>
        {
            var running = await Queries.ListAsync(new(Status: JobStatus.Running, Limit: IdleScanLimit), ct);
            var queued = await Queries.ListAsync(new(Status: JobStatus.Queued, Limit: IdleScanLimit), ct);
            busy = [.. running, .. queued.Where(job => job.AvailableAt <= UtcNow)];

            return busy.Count == 0;
        }, () => $"{busy.Count} job(s) are still running or due, such as {string.Join(", ", busy.Take(5).Select(job => $"{job.Contract} ({job.Status})"))}."
            + UnservedQueues(busy), timeout, cancellationToken);
    }

    /// <summary>Points at queued jobs in queues this host's worker does not serve, the usual reason a wait times out.</summary>
    private string UnservedQueues(IEnumerable<JobRecord> jobs)
    {
        var unserved = jobs.Where(job => job.Status == JobStatus.Queued && !queues.Contains(job.Queue)).Select(job => job.Queue).Distinct().ToArray();

        return unserved.Length == 0 ? "" : $" Queued work waits in queues this host does not serve: {string.Join(", ", unserved)}. Pass them as queues.";
    }

    private static async Task WaitAsync(Func<CancellationToken, Task<bool>> done, Func<string> describe, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            while (!await done(deadline.Token))
            {
                await Task.Delay(PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {timeout ?? DefaultTimeout}. {describe()}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
