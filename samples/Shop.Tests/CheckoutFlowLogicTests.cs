using Shop;
using WorkThrough;
using WorkThrough.Testing;
using Xunit;

namespace Shop.Tests;

/// <summary>
/// The checkout flow's logic with stubbed jobs: which steps run, in what order, with which inputs. No payment, stock or shipping
/// code runs, so these tests need none of their dependencies.
/// </summary>
public sealed class CheckoutFlowLogicTests
{
    private static readonly string[] Queues = [JobDefaults.Queue, ShopJobs.ShippingQueue];

    private static Task<WorkThroughTestHost> StartAsync(bool needsReview) => WorkThroughTestHost.StartAsync(workthrough => workthrough
        .Stub(ShopJobs.ReserveStock)
        .Stub(ShopJobs.ChargePayment, (ChargePayment charge) => new Payment(charge.OrderId, needsReview))
        .Stub(ShopJobs.ShipOrder)
        .Stub(ShopJobs.NotifyCustomer), queues: Queues);

    [Fact]
    public async Task A_payment_without_review_ships_at_once()
    {
        await using var host = await StartAsync(needsReview: false);

        var flow = (await host.RunFlowAsync(CheckoutFlow.Definition, new OrderPlaced("order-1", "tenant-a", "ada", 40m))).AssertSucceeded();

        Assert.Equal(new ChargePayment("order-1", 40m), flow.Step("charge").Input<ChargePayment>());
        Assert.Equal(JobStatus.Skipped, flow.Step("review").Status);
        Assert.Equal("order-1", flow.Step("ship").Input<ShipOrder>().OrderId);
    }

    [Fact]
    public async Task A_payment_that_needs_review_waits_for_approval_before_shipping()
    {
        await using var host = await StartAsync(needsReview: true);
        var run = await host.Flows.StartAsync(CheckoutFlow.Definition, new OrderPlaced("order-2", "tenant-a", "alan", 900m));
        await host.WaitUntilIdleAsync();
        Assert.Equal(JobStatus.Waiting, (await host.FlowResultAsync(run)).Step("ship").Status);

        await host.Flows.SignalAsync(run, CheckoutFlow.ReviewApproved, new ReviewApproved("order-2", "grace"), "review:order-2");

        (await host.WaitForFlowAsync(run)).AssertSucceeded();
    }

    [Fact]
    public async Task A_declined_payment_stops_for_an_operator_before_shipping()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .Stub(ShopJobs.ReserveStock)
            .Reject(ShopJobs.ChargePayment, "card declined")
            .Stub(ShopJobs.ShipOrder)
            .Stub(ShopJobs.NotifyCustomer), queues: Queues);

        var flow = await host.RunFlowAsync(CheckoutFlow.Definition, new OrderPlaced("order-3", "tenant-a", "ada", 40m));

        flow.AssertStatus(FlowStatus.Incident);
        Assert.Equal(JobStatus.Waiting, flow.Step("ship").Status);
    }
}
