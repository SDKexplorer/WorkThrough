using System.Diagnostics;

namespace WorkThrough.Exercises;

/// <summary>
/// A PostgreSQL disaster-recovery drill. Workers process a backlog of jobs and flows; an online pg_dump is taken mid-run; the original
/// store is abandoned with leases outstanding; the dump is restored into a new schema, and fresh workers must finish every job and flow
/// with exactly one successful attempt each and a gap-free journal. Requires the docker CLI and the test database container.
/// </summary>
internal static class RestoreSuite
{
    private const int Jobs = 1500;
    private const int Flows = 150;
    private const int Workers = 2;
    private const int HandlerDelayMs = 20;
    private const string Container = "workthrough-postgres-test";

    public static async Task RunAsync(string provider)
    {
        if (provider != "postgres")
        {
            throw new NotSupportedException("The restore drill uses pg_dump and runs on PostgreSQL.");
        }

        await using var original = await ExerciseDatabase.CreateAsync("postgres");
        var correlation = Guid.NewGuid().ToString("N");
        var client = new JobClient(original.Store);
        var flow = new FlowBuilder("exercises.restore", 1)
            .Step("first", Workload.Fast, new WorkItem(0, HandlerDelayMs), Workload.Queue)
            .Step("second", Workload.Fast, new WorkItem(1, HandlerDelayMs), Workload.Queue).After("first").Build();
        await Parallel.ForEachAsync(Enumerable.Range(0, Jobs), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (index, ct) =>
            await client.EnqueueAsync(Workload.Fast, new WorkItem(index, HandlerDelayMs), new(Queue: Workload.Queue, CorrelationId: correlation), ct));
        for (var index = 0; index < Flows; index++)
        {
            await new WorkflowClient(original.Store).StartAsync(flow, new WorkItem(index), correlation);
        }

        var expected = Jobs + Flows * 2;
        var workers = Enumerable.Range(0, Workers).Select(_ => new WorkerProcess(original)).ToArray();
        await Task.WhenAll(workers.Select(worker => worker.ReadyAsync()));
        while (await SucceededAsync(original) < expected / 3)
        {
            await Task.Delay(50);
        }

        // An online backup while work is running, then a hard stop: running attempts stay leased in the backup.
        var clock = Stopwatch.StartNew();
        var restoredSchema = "wt_restore_" + Guid.NewGuid().ToString("N");
        var target = new Npgsql.NpgsqlConnectionStringBuilder(original.ConnectionString);
        var dump = await Docker.RunAsync($"exec {Container} pg_dump -U {target.Username} -d {target.Database} -n {original.Schema} --no-owner", null);
        foreach (var worker in workers)
        {
            worker.Dispose();
        }

        var completedBefore = await SucceededAsync(original);
        await Docker.RunAsync($"exec -i {Container} psql -U {target.Username} -d {target.Database} -v ON_ERROR_STOP=1 -q",
            dump.Replace(original.Schema!, restoredSchema, StringComparison.Ordinal));
        var backupSeconds = clock.Elapsed.TotalSeconds;
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(original.ConnectionString) { SearchPath = restoredSchema }.ToString();
        var directory = Directory.CreateDirectory(Path.Combine(original.DirectoryPath, "restored")).FullName;
        await using var restored = new ExerciseDatabase("postgres", connection, restoredSchema, directory);
        await restored.Store.InitializeAsync();
        var inBackup = await SucceededAsync(restored);

        var replacements = Enumerable.Range(0, Workers).Select(_ => new WorkerProcess(restored)).ToArray();
        try
        {
            await Task.WhenAll(replacements.Select(worker => worker.ReadyAsync()));
            var completed = await PerformanceSuite.WaitForSuccessAsync(restored, correlation, expected, replacements);
            foreach (var job in completed)
            {
                var attempts = await restored.Store.AttemptsAsync(job.Id);
                Ensure(attempts.Count(attempt => attempt.Status == AttemptStatus.Succeeded) == 1, $"Job {job.Id} has other than one successful attempt.");
            }

            var events = new List<long>();
            for (var page = await restored.Store.ReadEventsAsync(0, 500); page.Count > 0; page = await restored.Store.ReadEventsAsync(events[^1], 500))
            {
                events.AddRange(page.Select(item => item.Sequence));
            }

            Ensure(events.SequenceEqual(Enumerable.Range(1, events.Count).Select(number => (long)number)), "The restored journal has gaps or disorder.");
            // Work that finished after the backup is not in it, so it runs again: delivery is at least once, and effects need their keys.
            Console.WriteLine($"PASS postgres restore: {expected} jobs and flow steps; {inBackup} had succeeded in the backup and {completedBefore - inBackup} " +
                $"more before the outage, which ran again after the restore. Every job finished with one successful attempt and a gap-free journal of " +
                $"{events.Count} events. Backup and restore took {backupSeconds:F1} s.");
        }
        finally
        {
            foreach (var worker in replacements)
            {
                worker.Dispose();
            }
        }
    }

    /// <summary>Succeeded jobs in the store; each drill uses a store of its own.</summary>
    private static async Task<long> SucceededAsync(ExerciseDatabase database) =>
        (await database.Store.StatisticsAsync()).Counts.GetValueOrDefault(JobStatus.Succeeded);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
