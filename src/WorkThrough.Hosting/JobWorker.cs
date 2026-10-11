using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WorkThrough;

/// <summary>Telemetry outcome of one attempt, including results that were never committed.</summary>
internal enum AttemptOutcome
{
    InfrastructureFailure, LostOwnership, Interrupted, Succeeded, Failed, Rejected, Deferred
}

internal sealed class JobWorker(
    IJobStore store, JobCatalog catalog, IServiceScopeFactory scopes,
    IOptionsMonitor<JobWorkerOptions> options, WorkerStatus status, ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(5);
    private const int LeaseRenewalsPerDuration = 3;
    private const string ShutdownReason = "Host shutdown interrupted execution";
    private const string DeadlineMessage = "The configured execution deadline elapsed.";

    private long registryRevision;
    private CompletionBatcher? completions;
    private DateTimeOffset nextHeartbeat = DateTimeOffset.MinValue;
    private bool protocolCurrent;
    private string? reportedBlockers;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (catalog.Registrations.Count == 0)
        {
            throw new InvalidOperationException("A worker requires jobs. Use AddJob or AddGeneratedJobs before starting it.");
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
        // Disposed after every running attempt below has been observed, so shutdown settlements still commit.
        await using var batcher = completions = new CompletionBatcher(store, SettlementTimeout);
        var running = new Dictionary<Guid, (JobRecord Job, Task Task)>();
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
                await HeartbeatAsync(settings, running.Count, stoppingToken);
                var claimed = await ClaimAsync(settings, running.Values.Select(x => x.Job).ToArray(), stoppingToken);
                foreach (var job in claimed)
                {
                    // A revoked attempt can still be physically running when a replacement is admitted.
                    running.Add(job.LeaseToken!.Value, (job, Task.Run(() => ExecuteAttemptAsync(job, settings, stoppingToken), CancellationToken.None)));
                }

                if (claimed.Count > 0)
                {
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

    private async Task HeartbeatAsync(JobWorkerOptions settings, int running, CancellationToken ct)
    {
        if (store is not IJobDiagnostics diagnostics || DateTimeOffset.UtcNow < nextHeartbeat)
        {
            return;
        }

        try
        {
            var processors = catalog.Registrations.Select(r => r.Identity).ToArray();
            var descriptions = catalog.Registrations
                .Select(r => new ProcessorDescription(r.Identity, r.CanonicalIdentity, r.DisplayName) { UpcastTo = r.UpcastTo }).ToArray();
            var heartbeat = new WorkerHeartbeat(settings.WorkerId, default, processors, settings.Queues, settings.MaxConcurrentJobs, running, descriptions)
            {
                Acquisition = status.Acquisition,
                Protocol = EngineProtocol.Supported
            };
            await diagnostics.HeartbeatAsync(heartbeat, ct);
            await ActivateProtocolAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Worker heartbeat failed");
        }

        nextHeartbeat = DateTimeOffset.UtcNow + HeartbeatInterval;
    }

    /// <summary>
    /// Raises the store to this build's protocol once the fleet allows it (ADR-021). Until then the worker keeps the active semantics,
    /// and reports the workers that still block the upgrade whenever that set changes.
    /// </summary>
    private async Task ActivateProtocolAsync(CancellationToken ct)
    {
        if (protocolCurrent || store is not IEngineProtocol protocols)
        {
            return;
        }

        var state = await protocols.ActivateEngineProtocolAsync(EngineProtocol.Supported, ct);
        protocolCurrent = state.Active >= EngineProtocol.Supported;
        var blockers = string.Join(", ", state.Blockers);
        if (protocolCurrent)
        {
            logger.LogInformation("Engine protocol {Protocol} is active", state.Active);
        }
        else if (blockers != reportedBlockers)
        {
            logger.LogInformation("Engine protocol stays at {Active} until these workers upgrade or stop: {Blockers}", state.Active, blockers);
        }

        reportedBlockers = blockers;
    }

    /// <summary>Claims as many jobs as there are free local slots, within each local per-job limit, in one admission transaction.</summary>
    private async Task<IReadOnlyList<JobRecord>> ClaimAsync(JobWorkerOptions settings, IReadOnlyCollection<JobRecord> running, CancellationToken ct)
    {
        var admissible = AdmissibleProcessors(settings, running);
        var free = Math.Min(settings.MaxConcurrentJobs - running.Count, ClaimRequest.MaximumBatch);
        if (free <= 0 || admissible.Length == 0)
        {
            return [];
        }

        try
        {
            status.Acquisition = status.Acquisition with { LastAttemptAt = DateTimeOffset.UtcNow };
            var request = new ClaimRequest(settings.WorkerId, admissible, settings.Queues, settings.LeaseDuration, registryRevision)
            {
                MaximumJobs = free,
                Budgets = LocalBudgets(settings, running)
            };
            var jobs = await store.ClaimBatchAsync(request, ct);
            status.Acquisition = status.Acquisition with { LastSucceededAt = DateTimeOffset.UtcNow, ConsecutiveFailures = 0 };

            return jobs;
        }
        catch (ProcessorRegistryChangedException exception)
        {
            // Retrying cannot succeed. Running attempts finish, then the failed service stops the host for a restart.
            logger.LogCritical(exception, "Processor mappings changed after this worker started; it stopped acquiring work");
            status.Stopped = "Processor mappings changed after this worker started.";
            throw;
        }
        catch (EngineProtocolException exception)
        {
            // An older build would misread work under the newer protocol. Running attempts finish, then the host stops.
            logger.LogCritical(exception, "The store requires a newer WorkThrough; this worker stopped acquiring work");
            status.Stopped = exception.Message;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Job acquisition failed; storage will be retried");
            status.Acquisition = status.Acquisition with { ConsecutiveFailures = Math.Min(status.Acquisition.ConsecutiveFailures, int.MaxValue - 1) + 1 };

            return [];
        }
    }

    /// <summary>Remaining local capacity per limited job-key group, so a batch cannot exceed <see cref="JobWorkerOptions.PerJobLimits"/>.</summary>
    private ContractBudget[] LocalBudgets(JobWorkerOptions settings, IEnumerable<JobRecord> running)
    {
        var active = running.GroupBy(job => catalog.CapacityKey(job.Contract.Key)).ToDictionary(group => group.Key, group => group.Count());

        return settings.PerJobLimits.GroupBy(limit => catalog.CapacityKey(limit.Key)).Select(group =>
        {
            var keys = catalog.Registrations.Select(registration => registration.Identity.Key)
                .Where(key => catalog.CapacityKey(key) == group.Key).Distinct().ToArray();

            return new ContractBudget(keys, group.Min(limit => limit.Value) - active.GetValueOrDefault(group.Key));
        }).ToArray();
    }

    /// <summary>Processors whose local per-job limit still has room on this worker.</summary>
    private JobContractId[] AdmissibleProcessors(JobWorkerOptions settings, IEnumerable<JobRecord> running)
    {
        var active = running.GroupBy(job => catalog.CapacityKey(job.Contract.Key)).ToDictionary(group => group.Key, group => group.Count());

        return catalog.Registrations.Select(registration => registration.Identity).Where(identity =>
        {
            var group = catalog.CapacityKey(identity.Key);
            var count = active.GetValueOrDefault(group);

            return settings.PerJobLimits.Where(limit => catalog.CapacityKey(limit.Key) == group).All(limit => count < limit.Value);
        }).ToArray();
    }

    private async Task ExecuteAttemptAsync(JobRecord job, JobWorkerOptions settings, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = AttemptOutcome.InfrastructureFailure;
        // The attempt continues the producer's trace when one was captured at enqueue.
        using var activity = ActivityContext.TryParse(job.TraceParent, null, out var parent)
            ? WorkThroughTelemetry.Activities.StartActivity("workthrough.process", ActivityKind.Consumer, parent)
            : WorkThroughTelemetry.Activities.StartActivity("workthrough.process", ActivityKind.Consumer);
        activity?.SetTag("workthrough.processor", job.Contract.ToString());
        activity?.SetTag("workthrough.job.id", job.Id);
        activity?.SetTag("workthrough.correlation.id", job.CorrelationId);
        activity?.SetTag("workthrough.flow.id", job.WorkflowRunId);
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalCancellation = new CancellationTokenSource();
        var renewal = RenewLeaseAsync(job.Id, job.LeaseToken!.Value, settings.LeaseDuration, attemptCancellation, renewalCancellation.Token);
        try
        {
            outcome = await RunAttemptAsync(job, settings, attemptCancellation, activity);
        }
        catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
        {
            // Setup and metadata recording are part of the owned attempt too.
            outcome = stoppingToken.IsCancellationRequested ? await ReleaseOnShutdownAsync(job) : AttemptOutcome.LostOwnership;
        }
        finally
        {
            await renewalCancellation.CancelAsync();
            await renewal;
            var tags = new TagList { { "processor", job.Contract.Key }, { "outcome", outcome.ToString() } };
            WorkThroughTelemetry.Attempts.Add(1, tags);
            WorkThroughTelemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            activity?.SetTag("workthrough.outcome", outcome.ToString());
        }
    }

    private async Task<AttemptOutcome> RunAttemptAsync(JobRecord job, JobWorkerOptions settings, CancellationTokenSource attempt, Activity? activity)
    {
        var token = job.LeaseToken!.Value;
        await using var scope = scopes.CreateAsyncScope();
        var registration = catalog.Get(job.Contract);
        var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        await store.RecordHandlerAsync(job.Id, token, DescribeHandler(registration.HandlerType), attempt.Token);
        var context = new JobExecutionContext(job.Id, job.Attempt, job.CorrelationId, job.WorkflowRunId, job.StepId, token);
        var executionStarted = Stopwatch.GetTimestamp();
        if (store is IJobDiagnostics diagnostics)
        {
            if (!await diagnostics.BeginAttemptAsync(job.Id, token, settings.ExecutionTimeout, settings.ProgressTimeout, attempt.Token))
            {
                return AttemptOutcome.LostOwnership;
            }

            context = context with { Progress = new AttemptProgress(diagnostics, job.Id, token) };
        }

        using var deadline = new CancellationTokenSource();
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, deadline.Token);
        StartDeadline(settings.ExecutionTimeout, executionStarted, deadline);
        JobResult result;
        try
        {
            invocation.Token.ThrowIfCancellationRequested();
            result = await JobPipeline.InvokeAsync(scope.ServiceProvider, new(job, registration, context), handler, invocation.Token);
            result.Validate();
            invocation.Token.ThrowIfCancellationRequested();

            // Cancellation callbacks can be delayed by a busy thread pool. Elapsed budget is authoritative.
            if (settings.ExecutionTimeout is { } budget && Stopwatch.GetElapsedTime(executionStarted) >= budget)
            {
                throw new TimeoutException(DeadlineMessage);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !attempt.IsCancellationRequested)
        {
            var failure = deadline.IsCancellationRequested ? new TimeoutException(DeadlineMessage, exception) : exception;
            activity?.SetStatus(ActivityStatusCode.Error, "Attempt failed");
            logger.LogWarning("Job {JobId} ({JobKey}) attempt {Attempt} failed", job.Id, job.Contract.Key, job.Attempt);

            return await SettleFailureAsync(scope.ServiceProvider, job, settings, failure);
        }

        return await SettleResultAsync(scope.ServiceProvider, job, result);
    }

    private static string DescribeHandler(Type handler)
    {
        var assembly = handler.Assembly.GetName();

        return $"{handler.FullName}, {assembly.Name} ({assembly.Version})";
    }

    private static void StartDeadline(TimeSpan? timeout, long executionStarted, CancellationTokenSource deadline)
    {
        if (timeout is not { } budget)
        {
            return;
        }

        var remaining = budget - Stopwatch.GetElapsedTime(executionStarted);
        if (remaining <= TimeSpan.Zero)
        {
            deadline.Cancel();
        }
        else
        {
            deadline.CancelAfter(remaining);
        }
    }

    private async Task<AttemptOutcome> SettleFailureAsync(IServiceProvider services, JobRecord job, JobWorkerOptions settings, Exception exception)
    {
        var decision = services.GetRequiredService<IJobRetryPolicy>().Decide(job, exception);
        if (decision.Delay < TimeSpan.Zero || decision.Delay > JobLimits.MaximumDelay)
        {
            throw new InvalidOperationException("Retry delay is outside the supported range.");
        }

        var token = job.LeaseToken!.Value;
        var failure = settings.DescribeFailure(exception);
        using var timeout = new CancellationTokenSource(SettlementTimeout);
        var status = decision.Retry ? AttemptStatus.Failed : AttemptStatus.Rejected;
        var settled = status == AttemptStatus.Rejected
            ? await store.RejectAsync(job.Id, token, failure, timeout.Token)
            : store is IJobDiagnostics diagnostics
                ? await diagnostics.FailWithDetailsAsync(job.Id, token, failure, decision.Delay, timeout.Token)
                : await store.FailAsync(job.Id, token, failure.ToSummary(), decision.Delay, timeout.Token);

        return settled ? await NotifyAsync(services, job, status) : AttemptOutcome.LostOwnership;
    }

    private async Task<AttemptOutcome> SettleResultAsync(IServiceProvider services, JobRecord job, JobResult result)
    {
        using var timeout = new CancellationTokenSource(SettlementTimeout);
        var completion = new AttemptCompletion(job.Id, job.LeaseToken!.Value, result);
        if (!await (completions?.CompleteAsync(completion, timeout.Token) ?? store.CompleteAsync(completion.JobId, completion.LeaseToken, result, timeout.Token)))
        {
            logger.LogWarning("Job {JobId} lost ownership before completion; its result was not committed", job.Id);

            return AttemptOutcome.LostOwnership;
        }

        var status = result.Outcome switch
        {
            JobOutcome.Rejected => AttemptStatus.Rejected,
            JobOutcome.Deferred => AttemptStatus.Deferred,
            _ => AttemptStatus.Succeeded
        };

        return await NotifyAsync(services, job, status);
    }

    private async Task<AttemptOutcome> ReleaseOnShutdownAsync(JobRecord job)
    {
        using var timeout = new CancellationTokenSource(SettlementTimeout);

        return await store.ReleaseAsync(job.Id, job.LeaseToken!.Value, ShutdownReason, timeout.Token)
            ? AttemptOutcome.Interrupted
            : AttemptOutcome.LostOwnership;
    }

    private sealed record AttemptProgress(IJobDiagnostics Diagnostics, Guid Id, Guid Token) : IJobProgress
    {
        public Task<bool> ReportAsync(string? description, CancellationToken cancellationToken = default) =>
            Diagnostics.ReportProgressAsync(Id, Token, description, cancellationToken);
    }

    /// <summary>Calls observers after a committed settlement and returns its telemetry outcome. Observer faults never change it.</summary>
    private async Task<AttemptOutcome> NotifyAsync(IServiceProvider services, JobRecord job, AttemptStatus status)
    {
        var outcome = status switch
        {
            AttemptStatus.Succeeded => AttemptOutcome.Succeeded,
            AttemptStatus.Rejected => AttemptOutcome.Rejected,
            AttemptStatus.Deferred => AttemptOutcome.Deferred,
            _ => AttemptOutcome.Failed
        };
        IJobObserver[] observers;
        try
        {
            observers = services.GetServices<IJobObserver>().ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Post-settlement observers could not be resolved; the committed outcome is unchanged");

            return outcome;
        }

        foreach (var observer in observers)
        {
            try
            {
                using var timeout = new CancellationTokenSource(SettlementTimeout);
                await observer.OnSettledAsync(new(job.Id, job.Attempt, job.Contract, status), timeout.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Post-settlement observer failed for {JobId}; the committed outcome is unchanged", job.Id);
            }
        }

        return outcome;
    }

    /// <summary>
    /// Renews every third of the lease. A refused renewal means ownership is gone. A failed renewal is retried on the next tick,
    /// so a brief storage outage does not abandon healthy handlers; the attempt is cancelled once the lease may have expired.
    /// </summary>
    private async Task RenewLeaseAsync(Guid id, Guid token, TimeSpan duration, CancellationTokenSource attempt, CancellationToken stop)
    {
        var interval = duration / LeaseRenewalsPerDuration;
        var renewed = Stopwatch.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    if (!await store.RenewAsync(id, token, duration, stop))
                    {
                        await attempt.CancelAsync();

                        return;
                    }

                    renewed = Stopwatch.GetTimestamp();
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    // The next tick would come after the lease may already have expired.
                    if (Stopwatch.GetElapsedTime(renewed) + interval >= duration)
                    {
                        logger.LogError(exception, "Cannot renew job {JobId} before its lease may expire; requesting cancellation", id);
                        await attempt.CancelAsync();

                        return;
                    }

                    logger.LogWarning(exception, "Renewing job {JobId} failed; retrying while the lease remains valid", id);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
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
