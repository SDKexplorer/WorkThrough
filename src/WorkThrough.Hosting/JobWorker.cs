using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WorkThrough;

internal sealed class JobWorker(
    IJobStore store, JobCatalog catalog, IServiceScopeFactory scopes,
    IOptionsMonitor<JobWorkerOptions> options, ILogger<JobWorker> logger) : BackgroundService
{
    private long registryRevision;
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (catalog.Registrations.Count == 0)
        {
            throw new InvalidOperationException("A worker requires handlers. Use AddGeneratedJobs or AddJob before starting it.");
        }

        // Validate the actual deployment and scoped DI graph before claiming work.
        await using var scope = scopes.CreateAsyncScope();
        foreach (var registration in catalog.Registrations)
        {
            _ = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        }

        if (store is IProcessorRegistry registry)
        {
            var snapshot = await registry.ProcessorRegistryAsync(cancellationToken);
            catalog.ValidateRegistry(snapshot);
            registryRevision = snapshot.Revision;
        }
        else if (catalog.Aliases.Count > 0)
        {
            throw new NotSupportedException("Processor aliases require a provider with coordinated registry support.");
        }

        catalog.Seal();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<Guid, (JobRecord Job, Task Task)>();
        var nextHeartbeat = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var entry in running.Where(x => x.Value.Task.IsCompleted).ToArray())
                {
                    await ObserveAsync(entry.Value.Task);
                    running.Remove(entry.Key);
                }

                var settings = options.CurrentValue;
                if (store is IJobDiagnostics diagnostics && DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    try
                    {
                        await diagnostics.HeartbeatAsync(new(settings.WorkerId, default, catalog.Registrations.Select(x => x.Identity).ToArray(),
                            settings.Queues, settings.MaxConcurrentJobs, running.Count, catalog.Registrations.Select(r => new ProcessorDescription(r.Identity, r.CanonicalIdentity, r.DisplayName)).ToArray()), stoppingToken);
                        nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Worker heartbeat failed");
                        nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
                    }
                }

                var supported = catalog.Registrations.Select(x => x.Identity).Where(identity =>
                    settings.PerJobLimits.Where(limit => catalog.CapacityKey(limit.Key) == catalog.CapacityKey(identity.Key))
                        .All(limit => running.Values.Count(x => catalog.CapacityKey(x.Job.Contract.Key) == catalog.CapacityKey(identity.Key)) < limit.Value)).ToArray();
                JobRecord? job = null;
                if (running.Count < settings.MaxConcurrentJobs && supported.Length > 0)
                {
                    try
                    {
                        job = await store.ClaimAsync(new(settings.WorkerId, supported, settings.Queues, settings.LeaseDuration, registryRevision), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Job acquisition failed; storage will be retried");
                    }
                }

                if (job is not null)
                {
                    var claimed = job;
                    // A revoked attempt can still be physically running when a replacement is admitted.
                    running.Add(job.LeaseToken!.Value, (job, Task.Run(() => ExecuteAttemptAsync(claimed, settings, stoppingToken), CancellationToken.None)));
                    continue;
                }

                var delay = Task.Delay(settings.PollInterval, stoppingToken);
                if (running.Count == 0)
                {
                    await delay;
                }
                else
                {
                    await Task.WhenAny(running.Values.Select(x => x.Task).Append(delay));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var task in running.Values)
            {
                await ObserveAsync(task.Task);
            }
        }
    }

    private async Task ExecuteAttemptAsync(JobRecord job, JobWorkerOptions settings, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "InfrastructureFailure";
        using var activity = WorkThroughTelemetry.Activities.StartActivity("workthrough.process", ActivityKind.Consumer);
        activity?.SetTag("workthrough.processor", job.Contract.ToString());
        activity?.SetTag("workthrough.job.id", job.Id);
        activity?.SetTag("workthrough.correlation.id", job.CorrelationId);
        activity?.SetTag("workthrough.flow.id", job.WorkflowRunId);
        var token = job.LeaseToken!.Value;
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var executionDeadline = new CancellationTokenSource();
        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation.Token, executionDeadline.Token);
        using var renewalCancellation = new CancellationTokenSource();
        var renewal = RenewLeaseAsync(job.Id, token, settings.LeaseDuration, attemptCancellation, renewalCancellation.Token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var registration = catalog.Get(job.Contract);
            var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
            var description = $"{registration.HandlerType.FullName}, {registration.HandlerType.Assembly.GetName().Name} ({registration.HandlerType.Assembly.GetName().Version})";
            await store.RecordHandlerAsync(job.Id, token, description, attemptCancellation.Token);
            var context = new JobExecutionContext(job.Id, job.Attempt, job.CorrelationId, job.WorkflowRunId, job.StepId, token);
            var executionStarted = Stopwatch.GetTimestamp();
            if (store is IJobDiagnostics diagnostics)
            {
                if (!await diagnostics.BeginAttemptAsync(job.Id, token, settings.ExecutionTimeout, settings.ProgressTimeout, attemptCancellation.Token))
                {
                    return;
                }

                context = context with
                {
                    Progress = new AttemptProgress(diagnostics, job.Id, token)
                };
            }

            if (settings.ExecutionTimeout is { } timeout)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(executionStarted);
                if (remaining <= TimeSpan.Zero)
                {
                    executionDeadline.Cancel();
                }
                else
                {
                    executionDeadline.CancelAfter(remaining);
                }
            }

            JobResult result;
            try
            {
                invocationCancellation.Token.ThrowIfCancellationRequested();
                result = await JobPipeline.InvokeAsync(scope.ServiceProvider, new(job, registration, context), handler, invocationCancellation.Token);
                result.Validate();
                invocationCancellation.Token.ThrowIfCancellationRequested();
                // Cancellation callbacks can be delayed by a busy thread pool. Elapsed budget is authoritative.
                if (settings.ExecutionTimeout is { } budget && Stopwatch.GetElapsedTime(executionStarted) >= budget)
                {
                    throw new TimeoutException("The configured execution deadline elapsed.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !attemptCancellation.IsCancellationRequested)
            {
                using var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var effective = executionDeadline.IsCancellationRequested ? new TimeoutException("The configured execution deadline elapsed.", exception) : exception;
                var decision = scope.ServiceProvider.GetRequiredService<IJobRetryPolicy>().Decide(job, effective);
                if (decision.Delay < TimeSpan.Zero || decision.Delay > TimeSpan.FromDays(365))
                {
                    throw new InvalidOperationException("Retry delay is outside the supported range.");
                }

                var failure = settings.DescribeFailure(effective);
                if (!decision.Retry)
                {
                    outcome = await store.RejectAsync(job.Id, token, failure, settleTimeout.Token) ? "Rejected" : "LostOwnership";
                }
                else if (store is IJobDiagnostics details)
                {
                    outcome = await details.FailWithDetailsAsync(job.Id, token, failure, decision.Delay, settleTimeout.Token) ? "Failed" : "LostOwnership";
                }
                else
                {
                    outcome = await store.FailAsync(job.Id, token, $"{failure.ExceptionType}: {failure.Message}", decision.Delay, settleTimeout.Token) ? "Failed" : "LostOwnership";
                }

                if (outcome != "LostOwnership")
                {
                    await NotifyAsync(scope.ServiceProvider, job, outcome);
                }

                activity?.SetStatus(ActivityStatusCode.Error, "Attempt failed");
                logger.LogWarning("Job {JobId} ({JobKey}) attempt {Attempt} failed", job.Id, job.Contract.Key, job.Attempt);

                return;
            }

            using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await store.CompleteAsync(job.Id, token, result, completionTimeout.Token))
            {
                outcome = "LostOwnership";
                logger.LogWarning("Job {JobId} lost ownership before completion; its result was not committed", job.Id);
            }
            else
            {
                outcome = result.Outcome.ToString();
                await NotifyAsync(scope.ServiceProvider, job, outcome);
            }
        }
        catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
        {
            outcome = "LostOwnership";
            // Setup and metadata recording are part of the owned attempt too.
            if (stoppingToken.IsCancellationRequested)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (await store.ReleaseAsync(job.Id, token, "Host shutdown interrupted execution", releaseTimeout.Token))
                {
                    outcome = "Interrupted";
                }
            }
        }
        finally
        {
            await renewalCancellation.CancelAsync();
            await renewal;
            var tags = new TagList { { "processor", job.Contract.Key }, { "outcome", outcome } };
            WorkThroughTelemetry.Attempts.Add(1, tags);
            WorkThroughTelemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            activity?.SetTag("workthrough.outcome", outcome);
        }
    }

    private sealed record AttemptProgress(IJobDiagnostics Diagnostics, Guid Id, Guid Token) : IJobProgress
    {
        public Task<bool> ReportAsync(string? description, CancellationToken cancellationToken = default) =>
            Diagnostics.ReportProgressAsync(Id, Token, description, cancellationToken);
    }

    private async Task NotifyAsync(IServiceProvider services, JobRecord job, string outcome)
    {
        IJobObserver[] observers;
        try
        {
            observers = services.GetServices<IJobObserver>().ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Post-settlement observers could not be resolved; the committed outcome is unchanged");

            return;
        }

        foreach (var observer in observers)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await observer.OnSettledAsync(new(job.Id, job.Attempt, job.Contract, outcome), timeout.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Post-settlement observer failed for {JobId}; the committed outcome is unchanged", job.Id);
            }
        }
    }

    private async Task RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationTokenSource attempt, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(duration / 3);
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (await store.RenewAsync(id, token, duration, stop))
                {
                    continue;
                }

                await attempt.CancelAsync();

                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cannot renew job {JobId}; requesting cancellation", id);
            await attempt.CancelAsync();
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            // Settlement/storage faults leave the lease for recovery, rather than pretending the handler failed or succeeded.
            logger.LogError(exception, "Worker infrastructure failed; unsettled work remains recoverable by lease expiry");
        }
    }
}
