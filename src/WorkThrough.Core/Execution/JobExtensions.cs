namespace WorkThrough;

public sealed record JobInvocation(JobRecord Job, JobRegistration Registration, JobExecutionContext Execution);

public delegate Task<JobResult> JobContinuation();

/// <summary>Scoped per attempt; first registered is outermost. Call and await next exactly once on success.</summary>
public interface IJobMiddleware
{
    Task<JobResult> InvokeAsync(JobInvocation invocation, JobContinuation next, CancellationToken cancellationToken);
}

public interface IJobValidator<in TInput>
{
    Task ValidateAsync(TInput input, JobExecutionContext context, CancellationToken cancellationToken);
}

public sealed class JobRejectedException(string message) : Exception(message);

public sealed record RetryDecision(bool Retry, TimeSpan Delay)
{
    public static RetryDecision Stop { get; } = new(false, TimeSpan.Zero);

    public static RetryDecision After(TimeSpan delay) => new(true, delay);
}

public interface IJobRetryPolicy
{
    RetryDecision Decide(JobRecord job, Exception exception);
}

/// <summary>A committed attempt outcome, not a durable notification. Lost acknowledgments may suppress callbacks.</summary>
public sealed record AttemptSettlement(Guid JobId, int Attempt, JobContractId Processor, AttemptStatus Outcome);

public interface IJobObserver
{
    Task OnSettledAsync(AttemptSettlement settlement, CancellationToken cancellationToken);
}
