using WorkThrough;

namespace Shop.Jobs;

/// <summary>Turns an accepted order into its checkout flow. A retried attempt finds the existing run through the start key.</summary>
public sealed class StartCheckoutJob(WorkflowClient flows) : IJob<OrderPlaced>
{
    public async Task<JobResult> ExecuteAsync(OrderPlaced order, JobExecutionContext context, CancellationToken cancellationToken)
    {
        var run = await flows.StartAsync(CheckoutFlow.Definition, order, CheckoutFlow.Start(order), cancellationToken);

        return JobResult.From(new { order.OrderId, run });
    }
}

public sealed class ReserveStockJob : IJob<ReserveStock>
{
    public async Task<JobResult> ExecuteAsync(ReserveStock input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        return JobResult.From(new Reservation(input.OrderId, Reserved: true));
    }
}

/// <summary>Attempts can repeat after a crash, so the provider call carries <see cref="JobExecutionContext.IdempotencyKey"/>.</summary>
public sealed class ChargePaymentJob : IJob<ChargePayment>
{
    public async Task<JobResult> ExecuteAsync(ChargePayment input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        if (input.Amount <= 0)
        {
            // Invalid input never succeeds on retry; rejecting stops the flow without spending attempts.
            return JobResult.Reject("The order amount must be positive.");
        }

        await context.ReportProgressAsync("Authorizing with the payment provider", cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);

        return JobResult.From(new Payment(input.OrderId, NeedsReview: input.Amount >= CheckoutFlow.ReviewThreshold));
    }
}

public sealed class ShipOrderJob : IJob<ShipOrder>
{
    public async Task<JobResult> ExecuteAsync(ShipOrder input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        return JobResult.From(new Shipment(input.OrderId, TrackingNumber: $"TRK-{context.IdempotencyKey[..12].ToUpperInvariant()}"));
    }
}

/// <summary>This sample sends no email; a real sender would deduplicate on the idempotency key.</summary>
public sealed class NotifyCustomerJob : IJob<NotifyCustomer>
{
    public Task<JobResult> ExecuteAsync(NotifyCustomer input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new { input.OrderId, notified = true }));
}

public sealed class DailyReportJob : IJob<DailyReport>
{
    public Task<JobResult> ExecuteAsync(DailyReport input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new { input.Region, generatedAt = DateTimeOffset.UtcNow }));
}
