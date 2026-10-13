using System.Diagnostics;
using Npgsql;

namespace WorkThrough.Exercises;

/// <summary>
/// Two workers in separate containers share a PostgreSQL store over their own network. One is cut off from the database while it holds
/// leases. Its leases must expire and the other worker must finish that work; once reconnected, its late completions must be refused,
/// every job must end with exactly one successful attempt, and the reconnected worker must take new work. Requires the docker CLI.
/// </summary>
internal static class PartitionSuite
{
    private const string Network = "workthrough-partition";
    private const string Database = "workthrough-postgres-test";
    private const string DatabaseAlias = "postgres";
    private const string Image = "mcr.microsoft.com/dotnet/aspnet:8.0";
    private const int Jobs = 40;
    private const int LaterJobs = 20;
    private const int HandlerDelayMs = 1500;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);
    private static readonly string[] Workers = ["worker-a", "worker-b"];

    public static async Task RunAsync(string provider)
    {
        if (provider != "postgres")
        {
            throw new NotSupportedException("The partition exercise uses Docker networks with the PostgreSQL test container.");
        }

        await using var database = await ExerciseDatabase.CreateAsync("postgres");
        var inside = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Host = DatabaseAlias, Port = 5432 }.ToString();
        await Docker.RunAsync($"network create {Network}");
        try
        {
            await Docker.RunAsync($"network connect --alias {DatabaseAlias} {Network} {Database}");
            foreach (var worker in Workers)
            {
                var effects = Directory.CreateDirectory(Path.Combine(database.DirectoryPath, worker)).FullName;
                await Docker.RunAsync($"run -d --name {worker} --network {Network} -v {AppContext.BaseDirectory}:/app:ro -v {effects}:/effects " +
                    $"-e WT_EXERCISE_PROVIDER=postgres -e \"WT_EXERCISE_CONNECTION={inside}\" -e WT_EXERCISE_EFFECTS=/effects/effects.db " +
                    $"-e WT_EXERCISE_CRASH=1 -e WT_EXERCISE_WORKER_ID={worker} -e WT_EXERCISE_SLOTS=4 {Image} dotnet /app/WorkThrough.Exercises.dll worker");
            }

            foreach (var worker in Workers)
            {
                await WaitAsync(async () => (await Docker.RunAsync($"logs {worker}")).Contains("READY", StringComparison.Ordinal), $"{worker} did not start");
            }

            var client = new JobClient(database.Store);
            for (var index = 0; index < Jobs; index++)
            {
                await client.EnqueueAsync(Workload.Fast, new WorkItem(index, HandlerDelayMs), new(Queue: Workload.Queue));
            }

            await WaitAsync(async () => (await RunningAsync(database, "worker-b")).Count > 0, "worker-b never held a lease");
            var held = await RunningAsync(database, "worker-b");
            await Docker.RunAsync($"network disconnect {Network} worker-b");
            var partition = Stopwatch.StartNew();
            await WaitAsync(async () => await SucceededAsync(database) == Jobs, "the remaining worker did not finish the partitioned work");
            var takeover = partition.Elapsed;

            // Once worker-b reports in again, stop worker-a, so new work proves worker-b recovered on its own.
            var rejoined = DateTimeOffset.UtcNow;
            await Docker.RunAsync($"network connect {Network} worker-b");
            await WaitAsync(async () => (await database.Store.WorkersAsync()).Any(worker => worker.WorkerId == "worker-b" && worker.SeenAt > rejoined),
                "worker-b did not report in after reconnecting");
            await Docker.RunAsync("stop worker-a");
            for (var index = 0; index < LaterJobs; index++)
            {
                await client.EnqueueAsync(Workload.Fast, new WorkItem(Jobs + index, HandlerDelayMs / 5), new(Queue: Workload.Queue));
            }

            await WaitAsync(async () => await SucceededAsync(database) == Jobs + LaterJobs, "worker-b did not take work after reconnecting");

            foreach (var job in await database.Store.ListAsync(new(Limit: 500)))
            {
                var attempts = await database.Store.AttemptsAsync(job.Id);
                Ensure(attempts.Count(attempt => attempt.Status == AttemptStatus.Succeeded) == 1, $"Job {job.Id} has other than one successful attempt.");
            }

            foreach (var job in held)
            {
                var attempts = await database.Store.AttemptsAsync(job.Id);
                Ensure(attempts.Any(attempt => attempt.WorkerId == "worker-b" && attempt.Status != AttemptStatus.Succeeded),
                    $"Job {job.Id} lost its partitioned attempt.");
                Ensure(attempts.Single(attempt => attempt.Status == AttemptStatus.Succeeded).WorkerId == "worker-a",
                    $"Job {job.Id} was settled by the partitioned worker.");
            }

            Console.WriteLine($"PASS postgres partition: worker-b was cut off holding {held.Count} leases; worker-a finished them and the rest of the backlog " +
                $"{takeover.TotalSeconds:F1} s after the partition. " +
                $"worker-b's late completions were refused, all {Jobs + LaterJobs} jobs succeeded exactly once, and worker-b took new work after reconnecting.");
        }
        catch
        {
            Console.Error.WriteLine("worker-b log:\n" + await LogsAsync("worker-b"));

            throw;
        }
        finally
        {
            foreach (var worker in Workers)
            {
                await Ignore($"rm -f {worker}");
            }

            await Ignore($"network disconnect {Network} {Database}");
            await Ignore($"network rm {Network}");
        }
    }

    private static async Task<IReadOnlyList<JobRecord>> RunningAsync(ExerciseDatabase database, string worker) =>
        [.. (await database.Store.ListAsync(new(Status: JobStatus.Running, Limit: 500))).Where(job => job.WorkerId == worker)];

    private static async Task<long> SucceededAsync(ExerciseDatabase database) =>
        (await database.Store.StatisticsAsync()).Counts.GetValueOrDefault(JobStatus.Succeeded);

    private static async Task WaitAsync(Func<Task<bool>> done, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!await done())
        {
            Ensure(clock.Elapsed < Deadline, $"Timed out: {failure}.");
            await Task.Delay(250);
        }
    }

    private static async Task<string> LogsAsync(string worker)
    {
        try
        {
            return await Docker.RunAsync($"logs --tail 40 {worker}");
        }
        catch (InvalidOperationException error)
        {
            return error.Message;
        }
    }

    private static async Task Ignore(string arguments)
    {
        try
        {
            await Docker.RunAsync(arguments);
        }
        catch (InvalidOperationException)
        {
            // Cleanup of something that was never created.
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
