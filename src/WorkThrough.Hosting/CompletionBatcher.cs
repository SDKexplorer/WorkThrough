using System.Threading.Channels;

namespace WorkThrough;

/// <summary>
/// Commits a worker's successful attempts together: whatever completed while the previous batch was committing goes into the
/// next one, so a lone completion is never delayed and a busy worker pays one transaction for many.
/// </summary>
internal sealed class CompletionBatcher : IAsyncDisposable
{
    private const int MaximumBatch = 100;
    private readonly Channel<(AttemptCompletion Completion, TaskCompletionSource<bool> Settled)> pending =
        Channel.CreateUnbounded<(AttemptCompletion, TaskCompletionSource<bool>)>(new() { SingleReader = true });
    private readonly IJobStore store;
    private readonly TimeSpan timeout;
    private readonly Task loop;

    public CompletionBatcher(IJobStore store, TimeSpan timeout)
    {
        this.store = store;
        this.timeout = timeout;
        loop = RunAsync();
    }

    public Task<bool> CompleteAsync(AttemptCompletion completion, CancellationToken cancellationToken)
    {
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.Writer.TryWrite((completion, settled)))
        {
            throw new InvalidOperationException("This worker has stopped settling attempts.");
        }

        return settled.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        pending.Writer.TryComplete();
        await loop;
    }

    private async Task RunAsync()
    {
        var batch = new List<(AttemptCompletion Completion, TaskCompletionSource<bool> Settled)>();
        while (await pending.Reader.WaitToReadAsync())
        {
            while (batch.Count < MaximumBatch && pending.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                using var deadline = new CancellationTokenSource(timeout);
                var settled = await store.CompleteBatchAsync(batch.Select(item => item.Completion).ToArray(), deadline.Token);
                for (var index = 0; index < batch.Count; index++)
                {
                    batch[index].Settled.TrySetResult(settled[index]);
                }
            }
            catch (Exception exception)
            {
                // Nothing in the batch committed; each attempt stays recoverable through its lease, as a failed single settlement would.
                foreach (var item in batch)
                {
                    item.Settled.TrySetException(exception);
                }
            }

            batch.Clear();
        }
    }
}
