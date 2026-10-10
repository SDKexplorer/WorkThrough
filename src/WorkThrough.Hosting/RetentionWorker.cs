using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

/// <summary>One bounded batch per interval. Database policy must also be explicitly enabled.</summary>
internal sealed class RetentionWorker(IRetentionMaintenance maintenance, IExecutionArchives archives,
    IEnumerable<IArchiveDestination> destinations, RetentionWorkerOptions options, ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FailureEvidenceBudget = TimeSpan.FromSeconds(1);

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
                        using var evidenceBudget = new CancellationTokenSource(FailureEvidenceBudget);
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
