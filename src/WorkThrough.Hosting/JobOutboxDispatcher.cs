using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

internal sealed class JobOutboxDispatcher(IJobOutbox source, IJobStore destination, ILogger<JobOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(WorkThroughBuilder.DispatchInterval);
        do
        {
            try
            {
                await source.DispatchOutboxAsync(destination, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox delivery failed; accepted requests remain recoverable");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
