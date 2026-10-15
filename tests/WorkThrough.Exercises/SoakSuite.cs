using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkThrough.Storage;

namespace WorkThrough.Exercises;

/// <summary>
/// Long-running open-loop load under every admission policy. Each minute records throughput, backlog, latency, worker memory and
/// stored rows, and checks every limit against that window's recorded attempts; the run then drains and verifies single success.
/// </summary>
internal static class SoakSuite
{
    private const int WorkerProcesses = 3;
    private const int OfferedPerSecond = 25;
    private const int ProducerTicksPerSecond = 10;
    private const int Tenants = 10;
    private const int PartitionsPerTenant = 20;
    private const int TenantMaximum = 4;
    private const int PartitionMaximum = 1;
    private const int RatePermits = 5;
    private const int FlowWidth = 4;
    private const int MaxParallelSteps = 2;
    private const int HandlerDelayMs = 20;
    private const int FlowPercent = 10;
    private const int LimitedPercent = 10;
    private const string Correlation = "soak";
    private const int DeadlockVictim = 1205;
    private const int MeasurementAttempts = 5;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(string provider, int minutes, string output)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        var store = database.Store;
        await AdmissionSuite.RequireAsync(store.SetKeyedLimitAsync(new(KeyScope.Partition, null, PartitionMaximum), AdmissionSuite.Command("Soak partition limit")));
        await AdmissionSuite.RequireAsync(store.SetKeyedLimitAsync(new(KeyScope.Tenant, null, TenantMaximum), AdmissionSuite.Command("Soak tenant limit")));
        await AdmissionSuite.RequireAsync(store.SetRateLimitAsync(new(Workload.Limited.Identity.Key, RatePermits, RateWindow), AdmissionSuite.Command("Soak rate limit")));
        var workers = Enumerable.Range(0, WorkerProcesses).Select(_ => new WorkerProcess(database)).ToArray();
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.ReadyAsync()));
            await using var memory = new MemorySampler(workers, MemoryInterval);
            var flow = FanOut();
            var producer = new Producer(store, flow);
            var minutesSeries = new List<object>();
            var clock = Stopwatch.StartNew();
            using (var stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes)))
            {
                var producing = producer.RunAsync(stop.Token);
                var windowStart = await DatabaseNowAsync(database);
                for (var minute = 1; minute <= minutes; minute++)
                {
                    memory.Enter(minute.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(Window);
                    foreach (var worker in workers)
                    {
                        worker.CheckAlive();
                    }

                    if (producing.IsFaulted)
                    {
                        await producing;
                    }

                    var windowEnd = await DatabaseNowAsync(database);
                    var sample = await MeasureWindowAsync(database, workers, minute, windowStart, windowEnd, producer.Offered);
                    minutesSeries.Add(sample);
                    windowStart = windowEnd;
                }

                await producing;
            }

            var drainStarted = clock.Elapsed;
            await DrainAsync(database, workers);
            var drainSeconds = (clock.Elapsed - drainStarted).TotalSeconds;
            var outcome = await VerifyOutcomeAsync(database, producer.Offered, producer.FlowStarts);
            var report = new
            {
                recordedAt = DateTimeOffset.UtcNow,
                gitRevision = Environment.GetEnvironmentVariable("WT_GIT_REVISION") ?? "unrecorded",
                workingTree = Environment.GetEnvironmentVariable("WT_GIT_STATE") ?? "unrecorded",
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                logicalProcessors = Environment.ProcessorCount,
                databaseVersion = await database.VersionAsync(),
                workload = new
                {
                    minutes,
                    workerProcesses = WorkerProcesses,
                    localSlots = Workload.LocalSlots,
                    offeredPerSecond = OfferedPerSecond,
                    tenants = Tenants,
                    partitionsPerTenant = PartitionsPerTenant,
                    tenantMaximum = TenantMaximum,
                    partitionMaximum = PartitionMaximum,
                    ratePermitsPerSecond = RatePermits,
                    flowPercent = FlowPercent,
                    flowWidth = FlowWidth,
                    maxParallelSteps = MaxParallelSteps,
                    limitedPercent = LimitedPercent,
                    handlerDelayMs = HandlerDelayMs,
                    retention = "disabled; history accumulates"
                },
                outcome,
                drainSeconds,
                memory = memory.Report(),
                minutes = minutesSeries
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(JobJson.Options) { WriteIndented = true }));
            Console.WriteLine($"PASS {provider}: {minutes} min soak, {outcome.Jobs} jobs succeeded once, every window within its limits.");
        }
        finally
        {
            foreach (var worker in workers)
            {
                worker.Dispose();
            }
        }
    }

    private static FlowDefinition FanOut()
    {
        var builder = new FlowBuilder("exercises.soak-fanout", 1);
        for (var step = 0; step < FlowWidth; step++)
        {
            builder.Step($"branch-{step}", Workload.Fast, new WorkItem(step, HandlerDelayMs), Workload.Queue);
        }

        return builder.Build();
    }

    /// <summary>Open-loop arrivals: a fixed offered rate regardless of how fast work completes, so a slowing engine shows as backlog.</summary>
    private sealed class Producer(RelationalJobStore store, FlowDefinition flow)
    {
        private long offered;
        private long flowStarts;
        public long Offered => Interlocked.Read(ref offered);
        public long FlowStarts => Interlocked.Read(ref flowStarts);

        public async Task RunAsync(CancellationToken stop)
        {
            var client = new JobClient(store);
            var flows = new WorkflowClient(store);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / ProducerTicksPerSecond));
            var perTick = OfferedPerSecond / ProducerTicksPerSecond;
            var remainder = OfferedPerSecond % ProducerTicksPerSecond;
            long tick = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(stop))
                {
                    var count = perTick + (tick++ % ProducerTicksPerSecond < remainder ? 1 : 0);
                    await Task.WhenAll(Enumerable.Range(0, count).Select(_ => OfferAsync(client, flows)));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        private async Task OfferAsync(JobClient client, WorkflowClient flows)
        {
            var index = Interlocked.Increment(ref offered);
            var tenant = $"tenant-{index % Tenants}";
            var partition = $"{tenant}/p{index / Tenants % PartitionsPerTenant}";
            var kind = (int)(index % 100);
            if (kind < FlowPercent)
            {
                Interlocked.Increment(ref flowStarts);
                await flows.StartAsync(flow, new { index }, new FlowStart(Correlation, TenantId: tenant, MaxParallelSteps: MaxParallelSteps));
            }
            else
            {
                var definition = kind < FlowPercent + LimitedPercent ? Workload.Limited : Workload.Fast;
                await client.EnqueueAsync(definition, new WorkItem((int)index, HandlerDelayMs),
                    new(Queue: Workload.Queue, CorrelationId: Correlation, TenantId: tenant, PartitionKey: partition));
            }
        }
    }

    private sealed record Interval(DateTimeOffset Started, DateTimeOffset? Finished, string? Partition, string? Tenant, string? Run, string JobKey, long CreatedAt);

    /// <summary>Attempts active in the window, read straight from storage so the check covers every attempt without per-job lookups.</summary>
    private static async Task<object> MeasureWindowAsync(ExerciseDatabase database, WorkerProcess[] workers, int minute,
        long from, long until, long offered)
    {
        var intervals = await RetriedAsync(() => ReadIntervalsAsync(database, from, until));

        // Open attempts are still admitted, so they count as running until the end of the window.
        var attempts = intervals.Select(item => (Item: item, Attempt: new JobAttempt(Guid.Empty, 1, "soak", item.Started,
            item.Finished ?? DateTimeOffset.FromUnixTimeMilliseconds(until), AttemptStatus.Succeeded, null, null))).ToArray();
        int PeakBy(Func<Interval, string?> key) => attempts.Where(pair => key(pair.Item) is not null).GroupBy(pair => key(pair.Item))
            .Select(group => PerformanceSuite.Peak(group.Select(pair => pair.Attempt))).DefaultIfEmpty(0).Max();
        var partitionPeak = PeakBy(item => item.Partition);
        var tenantPeak = PeakBy(item => item.Tenant);
        var runPeak = PeakBy(item => item.Run);
        var rateWindowPeak = AdmissionSuite.SlidingWindowPeak(intervals.Where(item => item.JobKey == Workload.Limited.Identity.Key)
            .Select(item => item.Started), RateWindow);
        PerformanceSuite.Ensure(partitionPeak <= PartitionMaximum && tenantPeak <= TenantMaximum && runPeak <= MaxParallelSteps
            && rateWindowPeak <= RatePermits, $"Minute {minute} exceeded a limit: partition {partitionPeak}, tenant {tenantPeak}, run {runPeak}, rate {rateWindowPeak}.");

        var started = intervals.Where(item => item.Started.ToUnixTimeMilliseconds() >= from).ToArray();
        var completed = intervals.Count(item => item.Finished is { } finished && finished.ToUnixTimeMilliseconds() >= from);
        var waits = started.Select(item => (double)(item.Started.ToUnixTimeMilliseconds() - item.CreatedAt)).ToArray();
        var (backlog, rows) = await BacklogAsync(database);
        var sample = new
        {
            minute,
            offeredTotal = offered,
            startedJobs = started.Length,
            completedAttempts = completed,
            backlog,
            storedJobs = rows,
            queueWaitMs = waits.Length == 0 ? null : Distribution.Of(waits),
            partitionPeak,
            tenantPeak,
            runPeak,
            rateWindowPeak,
            workerResidentMiB = workers.Sum(worker => worker.ResidentBytes) / (1024.0 * 1024),
            workerCpuSeconds = workers.Sum(worker => worker.Cpu.TotalSeconds)
        };
        Console.WriteLine($"minute {minute}: offered {offered}, completed {completed}, backlog {backlog}, wait p95 {(waits.Length == 0 ? 0 : Distribution.Of(waits).P95):F0} ms, " +
            $"rss {sample.workerResidentMiB:F0} MiB, rows {rows}");

        return sample;
    }

    private static async Task<List<Interval>> ReadIntervalsAsync(ExerciseDatabase database, long from, long until)
    {
        var intervals = new List<Interval>();
        await using var connection = database.OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var prefix = database.Provider == "oracle" ? ":" : "@";
        command.CommandText = $"""
            SELECT a.started_at,a.finished_at,j.partition_key,j.tenant_id,j.workflow_run_id,j.job_key,j.created_at
            FROM dj_attempts a JOIN dj_jobs j ON j.id=a.job_id
            WHERE a.started_at<{prefix}window_end AND (a.finished_at IS NULL OR a.finished_at>={prefix}window_start)
            """;
        Parameter(command, "window_start", from);
        Parameter(command, "window_end", until);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            intervals.Add(new(Time(reader.GetValue(0))!.Value, Time(reader.GetValue(1)), Text(reader, 2), Text(reader, 3), Text(reader, 4),
                reader.GetString(5), Convert.ToInt64(reader.GetValue(6))));
        }

        return intervals;
    }

    private static Task<(long Backlog, long Rows)> BacklogAsync(ExerciseDatabase database) => RetriedAsync(async () =>
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(CASE WHEN status IN ('Queued','Running','Waiting') THEN 1 ELSE 0 END),0),COUNT(*) FROM dj_jobs";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)));
    });

    /// <summary>
    /// Repeats a measurement read chosen as a deadlock victim. Without READ_COMMITTED_SNAPSHOT, SQL Server reads take shared locks, so a
    /// large scan can deadlock with settlements; the store retries its own reads the same way.
    /// </summary>
    private static async Task<T> RetriedAsync<T>(Func<Task<T>> read)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read();
            }
            catch (SqlException error) when (error.Number == DeadlockVictim && attempt < MeasurementAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private static async Task DrainAsync(ExerciseDatabase database, WorkerProcess[] workers)
    {
        var deadline = Stopwatch.StartNew();
        while ((await BacklogAsync(database)).Backlog > 0)
        {
            PerformanceSuite.Ensure(deadline.Elapsed < DrainTimeout, "The backlog did not drain after the soak.");
            foreach (var worker in workers)
            {
                worker.CheckAlive();
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private sealed record SoakOutcome(long Jobs, long Offered, long FlowStarts, long NotSucceeded, long RetriedJobs);

    private static async Task<SoakOutcome> VerifyOutcomeAsync(ExerciseDatabase database, long offered, long flowStarts)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Separate scalar subqueries: Oracle rejects aggregates mixed with a scalar subquery in one select list.
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM dj_jobs),
              (SELECT COUNT(*) FROM dj_jobs WHERE status<>'Succeeded'),
              (SELECT COUNT(*) FROM (SELECT job_id FROM dj_attempts GROUP BY job_id HAVING COUNT(*)>1) retried)
            FROM dj_registry WHERE id=1
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var outcome = new SoakOutcome(Convert.ToInt64(reader.GetValue(0)), offered, flowStarts, Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2)));
        var expected = offered - flowStarts + flowStarts * FlowWidth;
        PerformanceSuite.Ensure(outcome.Jobs == expected, $"Expected {expected} jobs, found {outcome.Jobs}.");
        PerformanceSuite.Ensure(outcome.NotSucceeded == 0 && outcome.RetriedJobs == 0, $"{outcome.NotSucceeded} jobs did not succeed; {outcome.RetriedJobs} retried.");

        return outcome;
    }

    private static async Task<long> DatabaseNowAsync(ExerciseDatabase database)
    {
        var page = await database.Store.SearchExecutionsAsync(new(Limit: 1));

        return page.ObservedAt.ToUnixTimeMilliseconds();
    }

    private static void Parameter(DbCommand command, string name, object value)
    {
        // Oracle binds by position unless told otherwise, which would silently swap parameters used out of order.
        if (command is Oracle.ManagedDataAccess.Client.OracleCommand oracle)
        {
            oracle.BindByName = true;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset? Time(object value) => value is DBNull ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value));

    private static string? Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
