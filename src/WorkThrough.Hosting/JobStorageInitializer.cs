using Microsoft.Extensions.Hosting;

namespace WorkThrough;

internal sealed class StorageInitialization
{
    public bool Enabled { get; set; } = true;
    public List<ConcurrencyLimit> Limits { get; } = [];
}

internal sealed class JobStorageInitializer(IJobStore store, StorageInitialization initialization) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (initialization.Enabled)
        {
            await store.InitializeAsync(cancellationToken);
        }

        if (initialization.Limits.Count > 0)
        {
            await store.SeedLimitsAsync(initialization.Limits, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
