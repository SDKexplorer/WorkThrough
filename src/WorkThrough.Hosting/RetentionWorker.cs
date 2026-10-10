using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

public sealed class RetentionWorkerOptions
{
    public string Owner { get; set; } = $"retention:{Guid.NewGuid():N}";
    public OperationActor Actor { get; set; } = new("workthrough-maintenance", "retention-service");
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan BatchBudget { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>One bounded batch per interval. Database policy must also be explicitly enabled.</summary>
internal sealed class RetentionWorker(IRetentionMaintenance maintenance, IExecutionArchives archives,
    IEnumerable<IArchiveDestination> destinations, RetentionWorkerOptions options, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = destinations.ToDictionary(destination => destination.Id, StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested)
        {
            MaintenanceLease? lease = null;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(options.BatchBudget);
                var ct = budget.Token;
                lease = await maintenance.AcquireMaintenanceAsync(options.Owner, options.Actor, options.LeaseDuration, ct);
                if (lease is not null)
                {
                    foreach (var candidate in await maintenance.ReadExportCandidatesAsync(lease, ct))
                    {
                        try
                        {
                            if (lease.ExportDestination is null || !configured.TryGetValue(lease.ExportDestination, out var destination))
                            {
                                throw new InvalidOperationException("The policy export destination is not configured on this host.");
                            }

                            var export = await archives.ExportArchiveAsync(candidate.Id, ct);
                            if (export is not null)
                            {
                                await maintenance.RecordVerifiedExportAsync(lease, await destination.StoreAndVerifyAsync(lease.StoreId, export, ct), ct);
                            }
                        }
                        catch (StateConflictException) when (!ct.IsCancellationRequested)
                        {
                            logger.LogInformation("Archive {ArchiveId} retained because maintenance ownership, policy or hold state changed.", candidate.Id);
                        }
                        catch (Exception error) when (!ct.IsCancellationRequested)
                        {
                            await maintenance.RecordExportIncidentAsync(lease, candidate.Id, ct);
                            logger.LogWarning("Archive {ArchiveId} retained after export verification failed ({FailureType}).", candidate.Id, error.GetType().Name);
                        }
                    }

                    await maintenance.RunMaintenanceBatchAsync(lease, ct);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("Retention batch did not finish ({FailureType}); persisted ownership and batch receipts govern recovery.", error.GetType().Name);
                if (lease is not null)
                {
                    try
                    {
                        using var evidenceBudget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await maintenance.RecordMaintenanceFailureAsync(lease, error is OperationCanceledException, evidenceBudget.Token);
                    }
                    catch (Exception)
                    {
                        logger.LogWarning("Retention failure evidence could not be saved; inspect host logs and pending batch ownership.");
                    }
                }
            }

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

public static class RetentionHosting
{
    public static WorkThroughBuilder AddRetentionMaintenance(this WorkThroughBuilder builder, Action<RetentionWorkerOptions>? configure = null)
    {
        var options = new RetentionWorkerOptions();
        configure?.Invoke(options);
        if (options.PollInterval < TimeSpan.FromSeconds(1) || options.BatchBudget < TimeSpan.FromMilliseconds(100)
            || options.BatchBudget > TimeSpan.FromSeconds(30) || options.LeaseDuration < TimeSpan.FromSeconds(10)
            || options.LeaseDuration > TimeSpan.FromMinutes(5) || options.LeaseDuration <= options.BatchBudget
            || string.IsNullOrWhiteSpace(options.Owner) || options.Owner.Length > 200 || options.Actor is null
            || string.IsNullOrWhiteSpace(options.Actor.Issuer) || string.IsNullOrWhiteSpace(options.Actor.Subject))
        {
            throw new ArgumentException("Invalid retention host identity, interval, budget or lease duration.");
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<RetentionWorker>();

        return builder;
    }

    public static WorkThroughBuilder AddArchiveDirectory(this WorkThroughBuilder builder, string destinationId, string directory)
    {
        builder.Services.AddSingleton<IArchiveDestination>(new FileArchiveDestination(destinationId, directory));

        return builder;
    }
}
