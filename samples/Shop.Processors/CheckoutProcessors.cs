using WorkThrough;

namespace Shop.Processors;

/// <summary>Turns an accepted order into its checkout flow. A retried attempt finds the existing run through the start key.</summary>
public sealed class StartCheckout(WorkflowClient flows) : IProcessor<OrderPlaced>
{
    public async Task<JobResult> ProcessAsync(OrderPlaced order, JobExecutionContext context, CancellationToken cancellationToken)
    {
        var run = await flows.StartAsync(CheckoutFlow.Definition, order, CheckoutFlow.Start(order), cancellationToken);

        return JobResult.From(new { order.OrderId, run });
    }
}

public sealed class ReserveStockProcessor : IProcessor<ReserveStock>
{
    public async Task<JobResult> ProcessAsync(ReserveStock input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        return JobResult.From(new Reservation(input.OrderId, Reserved: true));
    }
}

/// <summary>Attempts can repeat after a crash, so the provider call carries <see cref="JobExecutionContext.IdempotencyKey"/>.</summary>
public sealed class ChargePaymentProcessor : IProcessor<ChargePayment>
{
    public async Task<JobResult> ProcessAsync(ChargePayment input, JobExecutionContext context, CancellationToken cancellationToken)
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

public sealed class ShipOrderProcessor : IProcessor<ShipOrder>
{
    public async Task<JobResult> ProcessAsync(ShipOrder input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        return JobResult.From(new Shipment(input.OrderId, TrackingNumber: $"TRK-{context.IdempotencyKey[..12].ToUpperInvariant()}"));
    }
}

/// <summary>This sample sends no email; a real sender would deduplicate on the idempotency key.</summary>
public sealed class NotifyCustomerProcessor : IProcessor<NotifyCustomer>
{
    public Task<JobResult> ProcessAsync(NotifyCustomer input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new { input.OrderId, notified = true }));
}

public sealed class DailyReportProcessor : IProcessor<DailyReport>
{
    public Task<JobResult> ProcessAsync(DailyReport input, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new { input.Region, generatedAt = DateTimeOffset.UtcNow }));
}
