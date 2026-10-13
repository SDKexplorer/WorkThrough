using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WorkThrough;

/// <summary>This host's worker acquisition state, shared by the worker and its health check.</summary>
internal sealed class WorkerStatus
{
    private WorkerAcquisition acquisition = new(null, null, 0);

    public WorkerAcquisition Acquisition
    {
        get => Volatile.Read(ref acquisition);
        set => Volatile.Write(ref acquisition, value);
    }

    /// <summary>Why the worker stopped acquiring for good; null while it runs.</summary>
    public string? Stopped { get; set; }
}

public static class WorkThroughHealthChecks
{
    public const string Storage = "workthrough-storage";
    public const string Worker = "workthrough-worker";

    /// <summary>Consecutive failed acquisitions after which the worker reports unhealthy.</summary>
    public const int FailingAcquisitions = 3;

    /// <summary>Adds a storage reachability check and, in hosts with a worker, a worker acquisition check.</summary>
    public static IHealthChecksBuilder AddWorkThrough(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<StorageHealthCheck>(Storage, tags: tags).AddCheck<WorkerHealthCheck>(Worker, tags: tags);
    }

    private sealed class StorageHealthCheck(IJobQueries queries) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // An indexed lookup of an ID that never exists: a round trip to the store, nothing more.
                _ = await queries.GetAsync(Guid.Empty, cancellationToken);

                return HealthCheckResult.Healthy("The job store answered.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new(context.Registration.FailureStatus, "The job store did not answer.", exception);
            }
        }
    }

    private sealed class WorkerHealthCheck(IServiceProvider services) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (services.GetService<WorkerStatus>() is not { } status)
            {
                return Task.FromResult(HealthCheckResult.Healthy("This host runs no worker."));
            }

            var acquisition = status.Acquisition;
            var data = new Dictionary<string, object>
            {
                ["consecutiveFailures"] = acquisition.ConsecutiveFailures,
                ["lastSucceededAt"] = acquisition.LastSucceededAt?.ToString("O") ?? "never"
            };
            var result = status.Stopped is { } reason ? new(context.Registration.FailureStatus, reason, data: data)
                : acquisition.ConsecutiveFailures >= FailingAcquisitions
                    ? new HealthCheckResult(context.Registration.FailureStatus, $"{acquisition.ConsecutiveFailures} consecutive acquisition failures.", data: data)
                    : HealthCheckResult.Healthy("The worker is acquiring work.", data);

            return Task.FromResult(result);
        }
    }
}
