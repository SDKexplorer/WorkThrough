using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

internal sealed class JobScheduler(IJobSchedules schedules, IServiceProvider services, ILogger<JobScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                await schedules.DispatchDueSchedulesAsync(cancellationToken: stoppingToken);
                if (services.GetService<IWorkflowSignals>() is { } signals)
                {
                    await signals.DispatchSignalsAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Schedule dispatch failed; persisted occurrences will be retried");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
