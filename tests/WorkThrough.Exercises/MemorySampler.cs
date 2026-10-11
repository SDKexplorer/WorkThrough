namespace WorkThrough.Exercises;

/// <summary>Samples each worker's resident memory; the OS reports no peak on every platform, so peaks come from samples.</summary>
internal sealed class MemorySampler : IAsyncDisposable
{
    private readonly WorkerProcess[] workers;
    private readonly CancellationTokenSource stop = new();
    private readonly List<(string Phase, long Bytes)> samples = [];
    private readonly Task loop;
    private volatile string phase = "start";

    public MemorySampler(WorkerProcess[] workers, TimeSpan interval)
    {
        this.workers = workers;
        Sample();
        loop = SampleAsync(interval);
    }

    public void Enter(string name)
    {
        phase = name;
        Sample();
    }

    public object Report()
    {
        lock (samples)
        {
            const double megabyte = 1024 * 1024;

            return new
            {
                unit = "MiB, sum of worker resident sets",
                start = samples[0].Bytes / megabyte,
                end = samples[^1].Bytes / megabyte,
                phases = samples.GroupBy(sample => sample.Phase).Select(group => new { phase = group.Key, peak = group.Max(sample => sample.Bytes) / megabyte })
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        Sample();
        stop.Dispose();
    }

    private async Task SampleAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stop.Token))
        {
            Sample();
        }
    }

    private void Sample()
    {
        var bytes = workers.Sum(worker => worker.ResidentBytes);
        lock (samples)
        {
            samples.Add((phase, bytes));
        }
    }
}
