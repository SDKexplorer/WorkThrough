using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WorkThrough.Exercises;

internal static class PerformanceSuite
{
    public static async Task RunAsync(string provider, string output, bool full)
    {
        var runs = new List<RunResult>();
        var count = full ? 500 : 60;
        var repetitions = full ? 3 : 1;
        for (var repeat = 1; repeat <= repetitions; repeat++)
        {
            foreach (var scenario in new[] { "mixed", "flows", "outbox" })
            {
                var result = await MeasureAsync(provider, scenario, count, repeat, full ? 100 : 12);
                runs.Add(result);
                Console.WriteLine($"{provider}/{scenario}/{repeat}: {result.JobsPerSecond:F1} jobs/s, e2e p95 {result.EndToEndMs.P95:F1} ms, {result.Jobs} jobs verified");
            }
        }

        var report = new
        {
            recordedAt = DateTimeOffset.UtcNow,
            gitRevision = Environment.GetEnvironmentVariable("WT_GIT_REVISION") ?? "unrecorded",
            workingTree = Environment.GetEnvironmentVariable("WT_GIT_STATE") ?? "unrecorded",
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            profile = full ? "full" : "smoke",
            workload = new
            {
                workerProcesses = 2,
                producers = 4,
                localSlots = 4,
                globalLimit = 6,
                limitedTypeLimit = 2,
                handlerDelayMs = 10,
                pollMs = 10,
                leaseSeconds = 30,
                load = "finite concurrent burst",
                repetitions,
                measuredOperations = count
            },
            runs
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(JobJson.Options) { WriteIndented = true }));
    }

    private static async Task<RunResult> MeasureAsync(string provider, string scenario, int count, int repeat, int warmup)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        await using var source = await ExerciseDatabase.CreateAsync(provider);
        await database.Store.SeedLimitsAsync([new(null, 6), new(Workload.Limited.Identity.Key, 2)]);
        using var first = new WorkerProcess(database);
        using var second = new WorkerProcess(database);
        await Task.WhenAll(first.ReadyAsync(), second.ReadyAsync());
        var workers = new[] { first, second };
        var idleCpu = workers.Sum(w => w.Cpu.TotalMilliseconds);
        var idleClock = Stopwatch.StartNew();
        await Task.Delay(1000);
        idleCpu = workers.Sum(w => w.Cpu.TotalMilliseconds) - idleCpu;
        var idleWall = idleClock.Elapsed.TotalMilliseconds;
        var client = new JobClient(database.Store);
        var warmIds = new List<Guid>();
        for (var i = 0; i < warmup; i++)
        {
            warmIds.Add(await client.EnqueueAsync(Workload.Fast, new WorkItem(i), new(Queue: Workload.Queue, CorrelationId: "warmup")));
        }

        await WaitForSuccessAsync(database, "warmup", warmup, workers);
        var acceptance = new ConcurrentBag<double>();
        var correlation = Guid.NewGuid().ToString("N");
        var flow = new FlowBuilder("exercises.sequence", 1)
            .Step("first", Workload.Fast, new WorkItem(0), Workload.Queue)
            .Step("second", Workload.Limited, new WorkItem(1), Workload.Queue).After("first").Build();
        var beforeCpu = workers.Sum(w => w.Cpu.TotalMilliseconds);
        using var parent = Process.GetCurrentProcess();
        var parentCpu = parent.TotalProcessorTime.TotalMilliseconds;
        using var scenarioDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var clock = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = scenarioDeadline.Token }, async (index, ct) =>
        {
            var start = Stopwatch.GetTimestamp();
            if (scenario == "flows")
            {
                await new WorkflowClient(database.Store).StartAsync(flow, new WorkItem(index), correlation, cancellationToken: ct);
            }
            else if (scenario == "outbox")
            {
                await source.Store.InTransactionAsync((_, _, jobs, token) =>
                jobs.SendAsync(Workload.Fast, new WorkItem(index), new(Queue: Workload.Queue, CorrelationId: correlation, IdempotencyKey: $"item:{index}"), token), ct);
            }
            else
            {
                await client.EnqueueAsync(index % 2 == 0 ? Workload.Fast : Workload.Limited, new WorkItem(index), new(Queue: Workload.Queue, CorrelationId: correlation), ct);
            }

            acceptance.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        });
        var producerSeconds = clock.Elapsed.TotalSeconds;
        if (scenario == "outbox")
        {
            while (await source.Store.DispatchOutboxAsync(database.Store, 100, scenarioDeadline.Token) > 0)
            {
            }
        }

        var expected = scenario == "flows" ? count * 2 : count;
        var completed = await WaitForSuccessAsync(database, correlation, expected, workers);
        var elapsed = clock.Elapsed.TotalSeconds;
        var cpu = workers.Sum(w => w.Cpu.TotalMilliseconds) - beforeCpu;
        var peakBytes = workers.Sum(w => w.PeakBytes);
        parentCpu = parent.TotalProcessorTime.TotalMilliseconds - parentCpu;
        var attempts = new List<(JobRecord Job, JobAttempt Attempt)>();
        foreach (var job in completed)
        {
            var history = await database.Store.AttemptsAsync(job.Id);
            Ensure(history.Count == 1 && history[0].Status == "Succeeded", "Unexpected retry or unsuccessful attempt under load.");
            Ensure(job.Output?.GetInt32() == job.Payload.GetProperty("index").GetInt32(), "Processor output mismatched its accepted input.");
            attempts.Add((job, history[0]));
        }

        var globalPeak = Peak(attempts.Select(x => x.Attempt));
        var limitedPeak = Peak(attempts.Where(x => x.Job.Contract == Workload.Limited.Identity).Select(x => x.Attempt));
        Ensure(globalPeak <= 6 && limitedPeak <= 2, "Shared admission limit exceeded across independent processes.");
        var queue = attempts.Select(x => (x.Attempt.StartedAt - x.Job.CreatedAt).TotalMilliseconds);
        var endToEnd = attempts.Select(x => (x.Attempt.FinishedAt!.Value - x.Job.CreatedAt).TotalMilliseconds);
        var execution = attempts.Select(x => (x.Attempt.FinishedAt!.Value - x.Attempt.StartedAt).TotalMilliseconds);

        return new(scenario, repeat, await database.VersionAsync(), expected, count, expected / elapsed, count / producerSeconds,
            Distribution.Of(acceptance), Distribution.Of(queue), Distribution.Of(endToEnd), Distribution.Of(execution), elapsed,
            cpu, parentCpu, peakBytes, idleCpu / idleWall * 100, globalPeak, limitedPeak);
    }

    internal static async Task<List<JobRecord>> WaitForSuccessAsync(ExerciseDatabase database, string correlation, int expected, params WorkerProcess[] workers)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (true)
        {
            foreach (var worker in workers)
            {
                worker.CheckAlive();
            }

            var jobs = new List<JobRecord>();
            for (var offset = 0; ; offset += 500)
            {
                var page = await database.Store.ListAsync(new(correlation, Limit: 500, Offset: offset), timeout.Token);
                jobs.AddRange(page);
                if (page.Count < 500)
                {
                    break;
                }
            }

            Ensure(jobs.Count <= expected, "More jobs than accepted operations.");
            Ensure(!jobs.Any(j => j.Status is JobStatus.Failed or JobStatus.Rejected or JobStatus.Expired), "A measured job failed.");
            if (jobs.Count == expected && jobs.All(j => j.Status == JobStatus.Succeeded))
            {
                return jobs;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static int Peak(IEnumerable<JobAttempt> attempts)
    {
        var points = attempts.SelectMany(a => new[] { (At: a.StartedAt, Delta: 1), (At: a.FinishedAt!.Value, Delta: -1) });
        var active = 0;
        var peak = 0;
        // Millisecond storage timestamps: settle before admitting another at the same instant.
        foreach (var point in points.OrderBy(p => p.At).ThenBy(p => p.Delta))
        {
            active += point.Delta;
            peak = Math.Max(peak, active);
        }

        return peak;
    }
    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record Distribution(double Min, double P50, double P95, double P99, double Max)
{
    public static Distribution Of(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double At(double percentile) => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

        return new(sorted[0], At(.50), At(.95), At(.99), sorted[^1]);
    }
}
internal sealed record RunResult(string Scenario, int Repetition, string DatabaseVersion, int Jobs, int Operations,
    double JobsPerSecond, double AcceptedOperationsPerSecond, Distribution AcceptanceMs, Distribution QueueWaitMs,
    Distribution EndToEndMs, Distribution AttemptMs, double WallSeconds, double WorkerCpuMs, double HarnessCpuMs,
    long SumWorkerPeakRssBytes, double IdleWorkerCpuPercentOfOneCore, int MaximumGlobalLeases, int MaximumLimitedLeases);
