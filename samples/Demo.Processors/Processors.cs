using Demo.Contracts;
using WorkThrough;

namespace Demo.Billing.Processors;

public sealed class OrderValidator : IJobHandler<ValidateOrder>
{
    public async Task<JobResult> HandleAsync(ValidateOrder job, JobExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);
        if (job.Amount < 0) throw new ArgumentOutOfRangeException(nameof(job.Amount), "Order amount cannot be negative.");
        return JobResult.From(new { job.OrderId, approved = job.Amount <= 1000 });
    }
}

public sealed class OrderDispatcher : IJobHandler<DispatchOrder>
{
    public async Task<JobResult> HandleAsync(DispatchOrder job, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // A real shipping API must use context.IdempotencyKey to deduplicate repeated attempts.
        await Task.Delay(700, cancellationToken);
        return JobResult.From(new { job.OrderId, status = "Dispatched" });
    }
}

public sealed class OrderRejector : IJobHandler<RejectOrder>
{
    public Task<JobResult> HandleAsync(RejectOrder job, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new { job.OrderId, status = "Rejected", reason = "Amount requires review" }));
}
