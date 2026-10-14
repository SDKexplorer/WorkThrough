using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

internal sealed class StorageInitialization
{
    public bool Enabled { get; set; } = true;
    public List<ConcurrencyLimit> Limits { get; } = [];
    public List<KeyedLimit> KeyedLimits { get; } = [];
    public List<RateLimit> RateLimits { get; } = [];
    public List<ScheduleDefinition> Schedules { get; } = [];
    public IPayloadProtector? PayloadProtector { get; set; }
}

internal sealed class JobStorageInitializer(IJobStore store, StorageInitialization initialization, ILogger<JobStorageInitializer> logger) : IHostedService
{
    private static readonly OperationActor SeedActor = new("workthrough", "startup-seed", "Startup schedule seeding");

    /// <summary>Part of every seeded command ID; changing it would let hosts of different releases seed the same schedule twice.</summary>
    private const string SeedSource = "schedule-seed";

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

        if (initialization.Schedules.Count > 0)
        {
            await SeedSchedulesAsync(store as IJobSchedules ?? throw new NotSupportedException("This provider does not support schedules."), cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Creates missing schedules through the audited lifecycle. Command IDs derive from the schedule, so hosts starting together
    /// deduplicate on the command receipt; a conflict means another host or an operator owns the schedule now.
    /// </summary>
    private async Task SeedSchedulesAsync(IJobSchedules schedules, CancellationToken ct)
    {
        var existing = (await schedules.SchedulesAsync(ct)).ToDictionary(schedule => (schedule.Definition.Key, schedule.Definition.Version));
        foreach (var definition in initialization.Schedules)
        {
            if (existing.TryGetValue((definition.Key, definition.Version), out var stored))
            {
                if (stored.Status == DefinitionStatus.Draft)
                {
                    logger.LogWarning("Seeded schedule {Key} v{Version} is still a draft; publish it from the dashboard", definition.Key, definition.Version);
                }

                continue;
            }

            var enabled = await schedules.CreatePublishedAsync(definition, enable: true, SeedActor, SeedSource, "Seed schedule from host configuration", ct);
            if (enabled.Error is { Type: ErrorType.Conflict } conflict)
            {
                logger.LogWarning("Seeded schedule {Key} v{Version} was left for an operator: {Reason}", definition.Key, definition.Version, conflict.Message);

                continue;
            }

            if (!enabled.IsSuccess)
            {
                throw new InvalidOperationException($"Schedule {definition.Key} v{definition.Version} could not be seeded: {enabled.Error!.Message}");
            }
        }
    }
}
