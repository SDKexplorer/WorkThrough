using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace WorkThrough;

internal sealed class JobWorker(
    IJobStore store, JobCatalog catalog, IServiceScopeFactory scopes,
    IOptionsMonitor<JobWorkerOptions> options, ILogger<JobWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (catalog.Registrations.Count == 0)
            throw new InvalidOperationException("A worker requires handlers. Use AddGeneratedJobs or AddJob before starting it.");
        // Validate the actual deployment and scoped DI graph before claiming work.
        await using var scope = scopes.CreateAsyncScope();
        foreach (var registration in catalog.Registrations)
            _ = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
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
                            settings.Queues, settings.MaxConcurrentJobs, running.Count), stoppingToken);
                        nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception) { logger.LogError(exception, "Worker heartbeat failed"); nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10); }
                }
                var supported = catalog.Registrations.Select(x => x.Identity).Where(identity =>
                    !settings.PerJobLimits.TryGetValue(identity.Key, out var maximum)
                    || running.Values.Count(x => x.Job.Contract.Key == identity.Key) < maximum).ToArray();
                JobRecord? job = null;
                if (running.Count < settings.MaxConcurrentJobs && supported.Length > 0)
                {
                    try
                    {
                        job = await store.ClaimAsync(new(settings.WorkerId, supported, settings.Queues, settings.LeaseDuration), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception) { logger.LogError(exception, "Job acquisition failed; storage will be retried"); }
                }
                if (job is not null)
                {
                    var claimed = job;
                    // A revoked attempt can still be physically running when a replacement is admitted.
                    running.Add(job.LeaseToken!.Value, (job, Task.Run(() => ExecuteAttemptAsync(claimed, settings, stoppingToken), CancellationToken.None)));
                    continue;
                }
                var delay = Task.Delay(settings.PollInterval, stoppingToken);
                if (running.Count == 0) await delay;
                else await Task.WhenAny(running.Values.Select(x => x.Task).Append(delay));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var task in running.Values) await ObserveAsync(task.Task);
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
                if (!await diagnostics.BeginAttemptAsync(job.Id, token, settings.ExecutionTimeout, settings.ProgressTimeout, attemptCancellation.Token)) return;
                context = context with { Progress = new AttemptProgress(diagnostics, job.Id, token) };
            }
            if (settings.ExecutionTimeout is { } timeout)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(executionStarted);
                if (remaining <= TimeSpan.Zero) executionDeadline.Cancel();
                else executionDeadline.CancelAfter(remaining);
            }
            JobResult result;
            try
            {
                invocationCancellation.Token.ThrowIfCancellationRequested();
                result = await registration.InvokeAsync(handler, job.Payload, context, invocationCancellation.Token);
                invocationCancellation.Token.ThrowIfCancellationRequested();
                // Cancellation callbacks can be delayed by a busy thread pool. Elapsed budget is authoritative.
                if (settings.ExecutionTimeout is { } budget && Stopwatch.GetElapsedTime(executionStarted) >= budget)
                    throw new TimeoutException("The configured execution deadline elapsed.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !attemptCancellation.IsCancellationRequested)
            {
                using var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(job.Attempt, 6))))
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(250));
                var failure = settings.DescribeFailure(executionDeadline.IsCancellationRequested
                    ? new TimeoutException("The configured execution deadline elapsed.", exception) : exception);
                if (store is IJobDiagnostics details)
                    outcome = await details.FailWithDetailsAsync(job.Id, token, failure, delay, settleTimeout.Token) ? "Failed" : "LostOwnership";
                else outcome = await store.FailAsync(job.Id, token, $"{failure.ExceptionType}: {failure.Message}", delay, settleTimeout.Token) ? "Failed" : "LostOwnership";
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
            else outcome = "Succeeded";
        }
        catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
        {
            outcome = "LostOwnership";
            // Setup and metadata recording are part of the owned attempt too.
            if (stoppingToken.IsCancellationRequested)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (await store.ReleaseAsync(job.Id, token, "Host shutdown interrupted execution", releaseTimeout.Token)) outcome = "Interrupted";
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

    private async Task RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationTokenSource attempt, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(duration / 3);
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (await store.RenewAsync(id, token, duration, stop)) continue;
                await attempt.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cannot renew job {JobId}; requesting cancellation", id);
            await attempt.CancelAsync();
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception)
        {
            // Settlement/storage faults leave the lease for recovery, rather than pretending the handler failed or succeeded.
            logger.LogError(exception, "Worker infrastructure failed; unsettled work remains recoverable by lease expiry");
        }
    }
}
