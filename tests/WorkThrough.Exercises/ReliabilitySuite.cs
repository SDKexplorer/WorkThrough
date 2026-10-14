
namespace WorkThrough.Exercises;

internal static class ReliabilitySuite
{
    public static async Task RunAsync(string provider)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        var effects = new EffectStore(Path.Combine(database.DirectoryPath, "effects"));
        var id = await new JobClient(database.Store).EnqueueAsync(Workload.Fast, new WorkItem(1, CrashAfterEffect: true), new(Queue: Workload.Queue, CorrelationId: "kill-proof"));
        using (var worker = new WorkerProcess(database, crashRecovery: true))
        {
            await worker.ReadyAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (await effects.CountAsync() == 0)
            {
                worker.CheckAlive();
                await Task.Delay(20, timeout.Token);
            }

            worker.Kill();
        }

        PerformanceSuite.Ensure((await database.Store.GetAsync(id))!.Status == JobStatus.Running, "The killed process must leave an unsettled lease.");
        var restarted = database;

        using var replacement = new WorkerProcess(restarted, crashRecovery: true);
        await replacement.ReadyAsync();
        await PerformanceSuite.WaitForSuccessAsync(restarted, "kill-proof", 1, replacement);
        var attempts = await restarted.Store.AttemptsAsync(id);
        PerformanceSuite.Ensure(attempts.Count == 2 && attempts[0].Status == AttemptStatus.Abandoned && attempts[1].Status == AttemptStatus.Succeeded, "Crash recovery did not preserve attempt history.");
        PerformanceSuite.Ensure(await effects.CountAsync() == 1, "The idempotent business effect was duplicated.");
        Console.WriteLine($"PASS {provider}: killed worker after effect, recovered expired lease, retained history and one effect.");
    }
}
