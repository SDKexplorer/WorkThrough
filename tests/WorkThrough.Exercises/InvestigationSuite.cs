using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WorkThrough.Exercises;

internal static class InvestigationSuite
{
    internal static async Task RunAsync(string provider, string output, int retained, int baselineSeconds, int investigativeSeconds)
    {
        if (retained is < 100 or > 1_000_000 || baselineSeconds is < 5 or > 7200 || investigativeSeconds is < 5 or > 7200)
        {
            throw new ArgumentOutOfRangeException(nameof(retained), "Use 100–1,000,000 retained jobs and 5–7,200 seconds per phase.");
        }

        var phases = new List<InvestigationPhase>();
        Exception? failure = null;
        try
        {
            phases.Add(await MeasureAsync(provider, retained, baselineSeconds, 0));
            phases.Add(await MeasureAsync(provider, retained, investigativeSeconds, 20));
        }
        catch (Exception error)
        {
            failure = error;
        }

        var report = new
        {
            recordedAt = DateTimeOffset.UtcNow,
            gitRevision = Environment.GetEnvironmentVariable("WT_GIT_REVISION") ?? "unrecorded",
            workingTree = Environment.GetEnvironmentVariable("WT_GIT_STATE") ?? "unrecorded",
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            retainedFixtureJobs = retained,
            fixture = "synthetic terminal jobs with one to three attempts; no delivery claim for seeded rows",
            authentication = "locally signed per-client JWTs over loopback HTTP; not external identity-provider qualification",
            client = "official C# MCP client over Streamable HTTP",
            accessSink = "in-memory count, no external audit I/O",
            workload = new
            {
                workerProcesses = 2,
                producerConcurrency = 4,
                batchJobs = 24,
                globalAdmission = 6,
                investigatorPauseMs = 250,
                concurrentHttpLimit = InvestigationHost.ConcurrentRequests,
                requestsPerMinutePerSubject = InvestigationHost.RequestsPerMinute
            },
            phases,
            throughputDegradationPercent = phases.Count == 2 ? 100 * (1 - phases[1].JobsPerSecond / phases[0].JobsPerSecond) : (double?)null,
            failureType = failure?.GetType().FullName
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(JobJson.Options) { WriteIndented = true }));
        if (failure is not null)
        {
            throw new InvalidOperationException($"Investigation qualification failed; partial report: {output}", failure);
        }

        PerformanceSuite.Ensure(phases.All(phase => phase.Failures == 0 && phase.RedactionViolations == 0), "Investigative requests failed; inspect the report before claiming qualification.");
    }

    private static async Task<InvestigationPhase> MeasureAsync(string provider, int retained, int seconds, int investigators)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 180));
        await RetainedHistoryFixture.SeedAsync(database, retained, lifetime.Token);
        var historyFrom = (await database.Store.GetExecutionContextAsync(lifetime.Token)).ObservedAt.AddHours(-3);
        var retainedAttempts = 6L * (retained / 3) + ((retained % 3) switch { 1 => 2, 2 => 5, _ => 0 });
        PerformanceSuite.Ensure((await database.Store.ReadRuntimeMetricsAsync(new(From: historyFrom), lifetime.Token)).FinishedAttempts == retainedAttempts,
            "The investigative metrics window does not contain the entire retained attempt fixture.");
        Console.WriteLine($"{provider}: seeded {retained} synthetic retained jobs; starting {seconds}s with {investigators} investigators.");
        await database.Store.SeedLimitsAsync([new(null, 6)], lifetime.Token);
        using var first = new WorkerProcess(database);
        using var second = new WorkerProcess(database);
        await Task.WhenAll(first.ReadyAsync(), second.ReadyAsync());
        await using var endpoint = await InvestigationHost.StartAsync(database, TimeSpan.FromSeconds(seconds + 180));
        var clients = new List<InvestigationClient>();
        var inspection = new InvestigationReaders(historyFrom, retainedAttempts);
        var resources = new List<object>();
        var accepted = new LatencyHistogram();
        var queueWait = new LatencyHistogram();
        var endToEnd = new LatencyHistogram();
        using var stopInvestigation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var readers = Array.Empty<Task>();
        try
        {
            for (var index = 0; index < investigators; index++)
            {
                clients.Add(await endpoint.ConnectAsync(index, lifetime.Token));
            }

            var startCpu = first.Cpu + second.Cpu;
            using var process = Process.GetCurrentProcess();
            var parentCpu = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            readers = clients.Select((client, index) => inspection.ReadAsync(client, index, stopInvestigation.Token)).ToArray();
            var client = new JobClient(database.Store);
            var completed = 0;
            var sampleAt = TimeSpan.Zero;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var correlation = Guid.NewGuid().ToString("N");
                await Parallel.ForEachAsync(Enumerable.Range(0, 24), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = lifetime.Token }, async (index, ct) =>
                {
                    var started = Stopwatch.GetTimestamp();
                    await client.EnqueueAsync(Workload.Fast, new WorkItem(index), new(Queue: Workload.Queue, CorrelationId: correlation), ct);
                    accepted.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                });
                IReadOnlyList<JobRecord> jobs;
                do
                {
                    first.CheckAlive();
                    second.CheckAlive();
                    jobs = await database.Store.ListAsync(new(correlation, Limit: 24), lifetime.Token);
                    PerformanceSuite.Ensure(jobs.Count == 24 && jobs.All(job => job.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Succeeded), "Live qualification work was lost or failed.");
                    if (jobs.Any(job => job.Status != JobStatus.Succeeded))
                    {
                        await Task.Delay(20, lifetime.Token);
                    }
                }
                while (jobs.Any(job => job.Status != JobStatus.Succeeded));

                foreach (var job in jobs)
                {
                    PerformanceSuite.Ensure(job.Attempt == 1 && job.Output?.GetInt32() == job.Payload.GetProperty("index").GetInt32(), "Measured work had duplicate attempts or incorrect output.");
                    var attempt = (await database.Store.AttemptsAsync(job.Id, lifetime.Token)).Single();
                    queueWait.Record((attempt.StartedAt - job.AvailableAt).TotalMilliseconds);
                    endToEnd.Record((attempt.FinishedAt!.Value - job.CreatedAt).TotalMilliseconds);
                }

                completed += jobs.Count;
                if (clock.Elapsed >= sampleAt)
                {
                    process.Refresh();
                    resources.Add(new { elapsedSeconds = clock.Elapsed.TotalSeconds, managedBytes = GC.GetTotalMemory(false), harnessRssBytes = process.WorkingSet64, workerPeakRssBytes = first.PeakBytes + second.PeakBytes });
                    Console.WriteLine($"{provider}/{investigators} clients: {clock.Elapsed.TotalSeconds:F0}s, {completed} verified jobs, {inspection.Failures} request failures.");
                    sampleAt = clock.Elapsed.Add(TimeSpan.FromSeconds(10));
                }
            }

            clock.Stop();
            stopInvestigation.Cancel();
            await Task.WhenAll(readers);
            var elapsed = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"{provider}: {completed} live jobs verified with {investigators} investigators, {completed / elapsed:F1} jobs/s, {inspection.Failures} request failures.");

            return new(investigators, seconds, elapsed, completed, completed / elapsed, await database.VersionAsync(),
                accepted.Snapshot(), queueWait.Snapshot(), endToEnd.Snapshot(),
                inspection.Queries,
                inspection.Failures, inspection.FailureTypes, inspection.RedactionViolations, inspection.MaximumResponseBytes, endpoint.AccessEvents,
                (first.Cpu + second.Cpu - startCpu).TotalMilliseconds, (process.TotalProcessorTime - parentCpu).TotalMilliseconds, resources);
        }
        finally
        {
            stopInvestigation.Cancel();
            await Task.WhenAll(readers);
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }
}

internal sealed record InvestigationPhase(int Investigators, int RequestedSeconds, double MeasuredSeconds, int VerifiedJobs,
    double JobsPerSecond, string DatabaseVersion, object Acceptance, object QueueWait, object EndToEnd,
    IReadOnlyDictionary<string, object> Queries, long Failures, IReadOnlyDictionary<string, long> FailureTypes,
    long RedactionViolations, long MaximumResponseBytes, long AccessEvents, double WorkerCpuMs, double HarnessCpuMs,
    IReadOnlyList<object> Resources);
