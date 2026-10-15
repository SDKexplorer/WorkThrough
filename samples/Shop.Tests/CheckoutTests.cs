using Shop;
using Shop.Jobs;
using WorkThrough;
using WorkThrough.Testing;
using Xunit;

namespace Shop.Tests;

/// <summary>The checkout flow on the real engine, as docs/testing.md shows.</summary>
public sealed class CheckoutTests
{
    [Fact]
    public async Task A_small_order_completes_its_checkout()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddShopJobs(), queues: ShopJobRegistration.Queues);

        var order = new OrderPlaced("order-1", "tenant-a", "ada", 40m);
        var run = await host.Flows.StartAsync(CheckoutFlow.Definition, order, CheckoutFlow.Start(order));

        var flow = (await host.WaitForFlowAsync(run)).AssertSucceeded();
        Assert.False(flow.Step("charge").Output<Payment>().NeedsReview);
    }

    [Fact]
    public async Task An_invalid_amount_stops_at_payment_for_an_operator()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough.AddShopJobs(), queues: ShopJobRegistration.Queues);

        var order = new OrderPlaced("order-2", "tenant-a", "alan", -5m);
        var flow = await host.WaitForFlowAsync(await host.Flows.StartAsync(CheckoutFlow.Definition, order, CheckoutFlow.Start(order)));

        flow.AssertStatus(FlowStatus.Incident);
        Assert.Equal(JobStatus.Rejected, flow.Step("charge").Status);
    }
}
