using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WorkThrough.Storage;

namespace WorkThrough.Exercises;

/// <summary>
/// ADR-018 admission evidence across independent worker processes: every limit is checked from recorded attempt intervals,
/// the cost of evaluating non-binding policies is measured under contention, and worker memory is sampled throughout.
/// </summary>
internal static class AdmissionSuite
{
    private const int WorkerProcesses = 3;
    private const int FleetSlots = WorkerProcesses * Workload.LocalSlots;
    private const string Bulk = "bulk";
    private const string Urgent = "urgent";
    private const string BusyTenant = "tenant-busy";
    private const int QuietTenants = 3;
    private const int QuietJobsPerTenant = 20;
    private const int BusyPartitions = 24;
    private const int KeyedJobs = 400;
    private const int KeyedDelayMs = 20;
    private const int OverheadRepetitions = 2;
    private const int PartitionMaximum = 1;
    private const int TenantMaximum = 3;
    private const int RatePermits = 10;
    private const int RateJobs = 50;
    private const int FanOutRuns = 6;
    private const int FanOutWidth = 16;
    private const int MaxParallelSteps = 3;
    private const int FanOutDelayMs = 30;
    private const int BulkWeight = 3;
    private const int FairnessJobsPerQueue = 90;
    private const double FairnessTolerance = 0.05;
    private const int Producers = 4;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FairnessRelease = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MemoryInterval = TimeSpan.FromMilliseconds(250);

    public static async Task RunAsync(string provider, string output)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        var store = database.Store;
        var workers = Enumerable.Range(0, WorkerProcesses).Select(_ => new WorkerProcess(database, [Workload.Queue, Bulk, Urgent])).ToArray();
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.ReadyAsync()));
            await using var memory = new MemorySampler(workers, MemoryInterval);

            memory.Enter("overhead");
            var overhead = new List<object>();
            for (var repetition = 1; repetition <= OverheadRepetitions; repetition++)
            {
                foreach (var policies in new[] { false, true })
                {
                    if (policies)
                    {
                        await SetNonBindingPoliciesAsync(store);
                    }

                    var run = await RunKeyedAsync(database, workers);
                    if (policies)
                    {
                        await RemovePoliciesAsync(store, rate: true);
                    }

                    overhead.Add(new { repetition, policies = policies ? "non-binding" : "none", jobsPerSecond = run.JobsPerSecond });
                    Console.WriteLine($"{provider}/overhead/{repetition}/{(policies ? "non-binding" : "none")}: {run.JobsPerSecond:F1} jobs/s");
                }
            }

            memory.Enter("keyed");
            await RequireAsync(store.SetKeyedLimitAsync(new(KeyScope.Partition, null, PartitionMaximum), Command("Serialize each partition")));
            await RequireAsync(store.SetKeyedLimitAsync(new(KeyScope.Tenant, null, TenantMaximum), Command("Bound each tenant")));
            var keyed = await RunKeyedAsync(database, workers);
            await RemovePoliciesAsync(store, rate: false);
            var partitionPeak = PeakBy(keyed.Attempts, item => item.Job.PartitionKey);
            var tenantPeak = PeakBy(keyed.Attempts, item => item.Job.TenantId);
            Ensure(partitionPeak <= PartitionMaximum, $"Partition limit exceeded across processes: {partitionPeak}.");
            Ensure(tenantPeak <= TenantMaximum, $"Tenant limit exceeded across processes: {tenantPeak}.");
            var quietWait = Distribution.Of(keyed.Attempts.Where(item => item.Job.TenantId != BusyTenant).Select(QueueWait));
            var busyWait = Distribution.Of(keyed.Attempts.Where(item => item.Job.TenantId == BusyTenant).Select(QueueWait));
            Console.WriteLine($"{provider}/keyed: partition peak {partitionPeak}, tenant peak {tenantPeak}, quiet wait p95 {quietWait.P95:F0} ms, busy wait p95 {busyWait.P95:F0} ms");

            memory.Enter("rate");
            await RequireAsync(store.SetRateLimitAsync(new(Workload.Limited.Identity.Key, RatePermits, RateWindow), Command("Bound the limited processor's start rate")));
            var rate = await RunAsync(database, workers, Enumerable.Range(0, RateJobs)
                .Select(index => (Workload.Limited, new WorkItem(index), new EnqueueOptions(Queue: Workload.Queue))).ToArray());
            await RequireAsync(store.RemoveRateLimitAsync(Workload.Limited.Identity.Key, null, Command("Remove the exercise rate limit")));
            var windowPeak = SlidingWindowPeak(rate.Attempts.Select(item => item.Attempt.StartedAt), RateWindow);
            Ensure(windowPeak <= RatePermits, $"Rate limit exceeded across processes: {windowPeak} starts in one window.");
            var startSpan = rate.Attempts.Max(item => item.Attempt.StartedAt) - rate.Attempts.Min(item => item.Attempt.StartedAt);
            Console.WriteLine($"{provider}/rate: {windowPeak} starts in the busiest window, {RateJobs / startSpan.TotalSeconds:F1} starts/s");

            memory.Enter("fan-out");
            var fanOut = await RunFanOutAsync(database, workers);
            var runPeak = PeakBy(fanOut.Attempts, item => item.Job.WorkflowRunId?.ToString());
            Ensure(runPeak <= MaxParallelSteps, $"Flow parallelism exceeded across processes: {runPeak}.");
            var fleetPeak = PerformanceSuite.Peak(fanOut.Attempts.Select(item => item.Attempt));
            Console.WriteLine($"{provider}/fan-out: run peak {runPeak}, fleet peak {fleetPeak}");

            memory.Enter("fairness");
            await RequireAsync(store.SetQueueWeightAsync(Bulk, BulkWeight, 0, Command("Weight the bulk queue")));
            var release = DateTimeOffset.UtcNow + FairnessRelease;
            var fairness = await RunAsync(database, workers, Enumerable.Range(0, FairnessJobsPerQueue * 2)
                .Select(index => (Workload.Fast, new WorkItem(index), new EnqueueOptions(Queue: index % 2 == 0 ? Bulk : Urgent, NotBefore: release))).ToArray());
            var share = ContendedShare(fairness.Attempts, Bulk);
            var expectedShare = BulkWeight / (double)(BulkWeight + 1);
            Ensure(Math.Abs(share.Share - expectedShare) <= FairnessTolerance, $"Weighted share {share.Share:F3} differs from {expectedShare:F3}.");
            Console.WriteLine($"{provider}/fairness: bulk share {share.Share:F3} of {share.Starts} contended starts (expected {expectedShare:F3})");

            var report = new
            {
                recordedAt = DateTimeOffset.UtcNow,
                gitRevision = Environment.GetEnvironmentVariable("WT_GIT_REVISION") ?? "unrecorded",
                workingTree = Environment.GetEnvironmentVariable("WT_GIT_STATE") ?? "unrecorded",
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                databaseVersion = await database.VersionAsync(),
                workload = new { workerProcesses = WorkerProcesses, localSlots = Workload.LocalSlots, pollMs = Workload.PollMilliseconds, producers = Producers },
                overhead = new { jobs = KeyedJobs, handlerDelayMs = KeyedDelayMs, runs = overhead },
                keyed = new
                {
                    jobs = keyed.Attempts.Count,
                    partitionMaximum = PartitionMaximum,
                    tenantMaximum = TenantMaximum,
                    partitionPeak,
                    tenantPeak,
                    fleetPeak = PerformanceSuite.Peak(keyed.Attempts.Select(item => item.Attempt)),
                    quietTenantQueueWaitMs = quietWait,
                    busyTenantQueueWaitMs = busyWait,
                    jobsPerSecond = keyed.JobsPerSecond
                },
                rate = new { jobs = RateJobs, permits = RatePermits, windowMs = RateWindow.TotalMilliseconds, busiestWindowStarts = windowPeak, startsPerSecond = RateJobs / startSpan.TotalSeconds },
                fanOut = new { runs = FanOutRuns, width = FanOutWidth, maxParallelSteps = MaxParallelSteps, runPeak, fleetPeak, jobsPerSecond = fanOut.JobsPerSecond },
                fairness = new { weight = BulkWeight, expectedShare, observedShare = share.Share, contendedStarts = share.Starts, tolerance = FairnessTolerance },
                memory = memory.Report()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(JobJson.Options) { WriteIndented = true }));
            Console.WriteLine($"PASS {provider}: partition, tenant, rate, flow-parallelism and weighted-fairness admission held across {WorkerProcesses} processes.");
        }
        finally
        {
            foreach (var worker in workers)
            {
                worker.Dispose();
            }
        }
    }

    /// <summary>One busy tenant spread over many partitions beside quiet tenants with namespaced partitions of their own.</summary>
    private static Task<Phase> RunKeyedAsync(ExerciseDatabase database, WorkerProcess[] workers)
    {
        var busyJobs = KeyedJobs - QuietTenants * QuietJobsPerTenant;
        var work = Enumerable.Range(0, busyJobs).Select(index => Keyed(index, BusyTenant, $"{BusyTenant}/p{index % BusyPartitions}"))
            .Concat(Enumerable.Range(0, QuietTenants * QuietJobsPerTenant).Select(index =>
            {
                var tenant = $"tenant-quiet-{index % QuietTenants}";

                return Keyed(busyJobs + index, tenant, $"{tenant}/p{index % 4}");
            }));

        return RunAsync(database, workers, work.ToArray());

        static (JobDefinition<WorkItem>, WorkItem, EnqueueOptions) Keyed(int index, string tenant, string partition) =>
            (Workload.Fast, new WorkItem(index, KeyedDelayMs), new EnqueueOptions(Queue: Workload.Queue, TenantId: tenant, PartitionKey: partition));
    }

    private static async Task<Phase> RunFanOutAsync(ExerciseDatabase database, WorkerProcess[] workers)
    {
        var builder = new FlowBuilder("exercises.fanout", 1);
        for (var step = 0; step < FanOutWidth; step++)
        {
            builder.Step($"branch-{step}", Workload.Fast, new WorkItem(step, FanOutDelayMs), Workload.Queue);
        }

        var flow = builder.Build();
        var correlation = Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        for (var run = 0; run < FanOutRuns; run++)
        {
            await new WorkflowClient(database.Store).StartAsync(flow, new { run }, new FlowStart(correlation, MaxParallelSteps: MaxParallelSteps));
        }

        return await CollectAsync(database, workers, correlation, FanOutRuns * FanOutWidth, clock);
    }

    private static async Task<Phase> RunAsync(ExerciseDatabase database, WorkerProcess[] workers,
        (JobDefinition<WorkItem> Definition, WorkItem Item, EnqueueOptions Options)[] work)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var client = new JobClient(database.Store);
        var clock = Stopwatch.StartNew();
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Producers }, async (item, ct) =>
            await client.EnqueueAsync(item.Definition, item.Item, item.Options with { CorrelationId = correlation }, ct));

        return await CollectAsync(database, workers, correlation, work.Length, clock);
    }

    private static async Task<Phase> CollectAsync(ExerciseDatabase database, WorkerProcess[] workers, string correlation, int expected, Stopwatch clock)
    {
        var completed = await PerformanceSuite.WaitForSuccessAsync(database, correlation, expected, workers);
        var elapsed = clock.Elapsed.TotalSeconds;
        var attempts = new List<(JobRecord Job, JobAttempt Attempt)>();
        foreach (var job in completed)
        {
            var history = await database.Store.AttemptsAsync(job.Id);
            Ensure(history.Count == 1 && history[0].Status == AttemptStatus.Succeeded, "Unexpected retry or unsuccessful attempt under admission limits.");
            attempts.Add((job, history[0]));
        }

        return new(attempts, expected / elapsed);
    }

    private static async Task SetNonBindingPoliciesAsync(RelationalJobStore store)
    {
        const int unreachable = 10_000;
        await RequireAsync(store.SetKeyedLimitAsync(new(KeyScope.Partition, null, unreachable), Command("Non-binding partition limit")));
        await RequireAsync(store.SetKeyedLimitAsync(new(KeyScope.Tenant, null, unreachable), Command("Non-binding tenant limit")));
        await RequireAsync(store.SetRateLimitAsync(new(null, unreachable, TimeSpan.FromMinutes(1), KeyScope.Tenant), Command("Non-binding tenant rate")));
    }

    private static async Task RemovePoliciesAsync(RelationalJobStore store, bool rate)
    {
        await RequireAsync(store.RemoveKeyedLimitAsync(KeyScope.Partition, null, Command("Remove partition limit")));
        await RequireAsync(store.RemoveKeyedLimitAsync(KeyScope.Tenant, null, Command("Remove tenant limit")));
        if (rate)
        {
            await RequireAsync(store.RemoveRateLimitAsync(null, KeyScope.Tenant, Command("Remove tenant rate")));
        }
    }

    private static double QueueWait((JobRecord Job, JobAttempt Attempt) item) => (item.Attempt.StartedAt - item.Job.CreatedAt).TotalMilliseconds;

    internal static int PeakBy(IEnumerable<(JobRecord Job, JobAttempt Attempt)> attempts, Func<(JobRecord Job, JobAttempt Attempt), string?> key) =>
        attempts.Where(item => key(item) is not null).GroupBy(key).Select(group => PerformanceSuite.Peak(group.Select(item => item.Attempt))).DefaultIfEmpty(0).Max();

    /// <summary>The most starts inside any half-open window (t - window, t], the interval a rate limit counts.</summary>
    internal static int SlidingWindowPeak(IEnumerable<DateTimeOffset> starts, TimeSpan window)
    {
        var sorted = starts.Order().ToArray();
        var peak = 0;
        var first = 0;
        for (var last = 0; last < sorted.Length; last++)
        {
            while (sorted[first] <= sorted[last] - window)
            {
                first++;
            }

            peak = Math.Max(peak, last - first + 1);
        }

        return peak;
    }

    /// <summary>The queue's share of starts while every queue still had unstarted work, the only period a weight can apply to.</summary>
    private static (double Share, int Starts) ContendedShare(IReadOnlyList<(JobRecord Job, JobAttempt Attempt)> attempts, string queue)
    {
        var totals = attempts.GroupBy(item => item.Job.Queue).ToDictionary(group => group.Key, group => group.Count());
        var started = totals.Keys.ToDictionary(key => key, _ => 0);
        var contended = 0;
        var selected = 0;
        foreach (var item in attempts.OrderBy(item => item.Attempt.StartedAt))
        {
            if (started.Any(pair => pair.Value == totals[pair.Key]))
            {
                break;
            }

            started[item.Job.Queue]++;
            contended++;
            selected += item.Job.Queue == queue ? 1 : 0;
        }

        return (selected / (double)contended, contended);
    }

    internal static AdministrativeCommand Command(string reason) => new(Guid.NewGuid(), new("admission-exercise", "harness"), reason);

    internal static async Task RequireAsync(Task<Result> command)
    {
        var result = await command;
        Ensure(result.IsSuccess, result.Error?.Message ?? "Command failed.");
    }

    internal static async Task RequireAsync<T>(Task<Result<T>> command)
    {
        var result = await command;
        Ensure(result.IsSuccess, result.Error?.Message ?? "Command failed.");
    }

    private static void Ensure(bool condition, string message) => PerformanceSuite.Ensure(condition, message);

    private sealed record Phase(List<(JobRecord Job, JobAttempt Attempt)> Attempts, double JobsPerSecond);
}
