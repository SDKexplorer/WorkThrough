using System.Diagnostics;

namespace WorkThrough.Exercises;

/// <summary>
/// Engine capacity with no admission policies: a preloaded backlog drained by several wide workers running near-instant handlers.
/// The result is bounded by storage write serialization, not by handler time or configured limits.
/// </summary>
internal static class ThroughputSuite
{
    private const int WorkerProcesses = 3;
    private const int SlotsPerWorker = 16;
    private const int HandlerDelayMs = 1;
    private const int Producers = 8;
    private const int SlotsPerTenant = 4;

    /// <param name="tenants">When positive, jobs spread over this many tenants, each limited to <see cref="SlotsPerTenant"/> running attempts.</param>
    public static async Task RunAsync(string provider, int jobs, int tenants)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        if (tenants > 0)
        {
            await database.Store.SeedAdmissionPoliciesAsync([new(KeyScope.Tenant, null, SlotsPerTenant)], []);
        }

        var client = new JobClient(database.Store);
        var correlation = Guid.NewGuid().ToString("N");
        var accept = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, jobs), new ParallelOptions { MaxDegreeOfParallelism = Producers }, async (index, ct) =>
            await client.EnqueueAsync(Workload.Fast, new WorkItem(index, HandlerDelayMs),
                new(Queue: Workload.Queue, CorrelationId: correlation, TenantId: tenants > 0 ? $"tenant-{index % tenants}" : null), ct));
        var enqueuedPerSecond = jobs / accept.Elapsed.TotalSeconds;
        var workers = Enumerable.Range(0, WorkerProcesses).Select(_ => new WorkerProcess(database, slots: SlotsPerWorker)).ToArray();
        try
        {
            var drain = Stopwatch.StartNew();
            await Task.WhenAll(workers.Select(worker => worker.ReadyAsync()));
            await PerformanceSuite.WaitForSuccessAsync(database, correlation, jobs, workers);
            Console.WriteLine($"{provider}/throughput: {jobs} jobs, enqueue {enqueuedPerSecond:F0}/s, drain {jobs / drain.Elapsed.TotalSeconds:F0} jobs/s " +
                $"({WorkerProcesses} workers x {SlotsPerWorker} slots, {HandlerDelayMs} ms handlers" +
                (tenants > 0 ? $", {tenants} tenants x {SlotsPerTenant} slots)" : ")"));
        }
        finally
        {
            foreach (var worker in workers)
            {
                worker.Dispose();
            }
        }
    }
}
