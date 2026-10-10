using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkThrough;

internal sealed class ExecutionEventSubscriber<THandler>(string name, long startAfter, IExecutionEvents events,
    IServiceScopeFactory scopes, ILogger<ExecutionEventSubscriber<THandler>> logger) : BackgroundService where THandler : class, IExecutionEventHandler
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cursor = await events.SubscribeAsync(name, startAfter, stoppingToken);
                foreach (var entry in await events.ReadEventsAsync(cursor, cancellationToken: stoppingToken))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(entry, stoppingToken);
                    if (!await events.AcknowledgeAsync(name, cursor, entry.Sequence, stoppingToken))
                    {
                        break;
                    }

                    cursor = entry.Sequence;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Subscription {Subscriber} did not advance; delivery will be retried", name);
            }

            await Task.Delay(WorkThroughBuilder.DispatchInterval, stoppingToken);
        }
    }
}
