namespace WorkThrough;

/// <summary>Background dispatch keeps going while full batches come back, so throughput is not capped at one batch per tick.</summary>
internal static class DispatchLoop
{
    public const int BatchSize = 100;

    // Bounds one tick, so a source that always reports a full batch cannot spin without pause.
    private const int MaximumBatchesPerTick = 50;

    public static async Task DrainAsync(Func<int, CancellationToken, Task<int>> dispatch, CancellationToken ct)
    {
        for (var batch = 0; batch < MaximumBatchesPerTick && await dispatch(BatchSize, ct) == BatchSize; batch++)
        {
        }
    }
}
