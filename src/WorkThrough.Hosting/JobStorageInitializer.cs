using Microsoft.Extensions.Hosting;

namespace WorkThrough;

internal sealed class StorageInitialization
{
    public bool Enabled { get; set; } = true;
    public List<ConcurrencyLimit> Limits { get; } = [];
    public List<KeyedLimit> KeyedLimits { get; } = [];
    public List<RateLimit> RateLimits { get; } = [];
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

        if (initialization.KeyedLimits.Count > 0 || initialization.RateLimits.Count > 0)
        {
            var policies = store as IAdmissionPolicies
                ?? throw new NotSupportedException("This provider does not support keyed or rate limits.");
            await policies.SeedAdmissionPoliciesAsync(initialization.KeyedLimits, initialization.RateLimits, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
