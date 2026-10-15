using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

internal sealed class JobScheduler(IJobSchedules schedules, IServiceProvider services, ILogger<JobScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(WorkThroughBuilder.DispatchInterval);
        do
        {
            try
            {
                await DispatchLoop.DrainAsync(schedules.DispatchDueSchedulesAsync, stoppingToken);
                if (services.GetService<IWorkflowSignals>() is { } signals)
                {
                    await DispatchLoop.DrainAsync(signals.DispatchSignalsAsync, stoppingToken);
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
