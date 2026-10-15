using WorkThrough;
using WorkThrough.Testing;
using Xunit;

namespace WorkThroughTests;

/// <summary>
/// The flow's logic with stubbed jobs: which steps run and what each receives. The engine, store and worker are real; only the
/// jobs are stand-ins, so no payment or shipping code runs.
/// </summary>
public sealed class OrderFlowTests
{
    [Fact]
    public async Task A_successful_charge_ships_with_its_payment()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .Stub(OrderJobs.Charge, (Charge charge) => new Receipt(charge.OrderId, "pay-1"))
            .Stub(OrderJobs.Ship)
            .Stub(OrderJobs.Apologise));

        var flow = (await host.RunFlowAsync(OrderFlow.Definition, new PlaceOrder("order-1", 40m))).AssertSucceeded();

        Assert.Equal(new Charge("order-1", 40m), flow.Step("charge").Input<Charge>());
        Assert.Equal(new Ship("order-1", "pay-1"), flow.Step("ship").Input<Ship>());
        Assert.Equal(JobStatus.Skipped, flow.Step("apologise").Status);
    }

    [Fact]
    public async Task A_declined_charge_apologises_instead_of_shipping()
    {
        await using var host = await WorkThroughTestHost.StartAsync(workthrough => workthrough
            .Reject(OrderJobs.Charge, "card declined")
            .Stub(OrderJobs.Ship)
            .Stub(OrderJobs.Apologise));

        var flow = (await host.RunFlowAsync(OrderFlow.Definition, new PlaceOrder("order-2", 40m))).AssertSucceeded();

        Assert.Equal(JobStatus.Skipped, flow.Step("ship").Status);
        Assert.Equal("order-2", flow.Step("apologise").Input<Apologise>().OrderId);
    }
}
