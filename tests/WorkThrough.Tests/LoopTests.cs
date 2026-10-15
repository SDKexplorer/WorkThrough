using Xunit;

namespace WorkThrough.Tests;

/// <summary>Child steps that repeat until their output meets a condition.</summary>
public sealed class LoopTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static ClaimRequest Claim<T>() => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1));

    // Each iteration passes its number and the previous iteration's output to the check.
    private static FlowDefinition Poll() => new FlowBuilder("tests.poll", 1).OnFailure(FlowFailurePolicy.Stop)
        .Step<Beta>("check", new { value = "check" }).BindInput("iteration", "iteration").BindInput("previous", "previous")
        .Build();

    private static FlowDefinition Shipment(int iterations) => new FlowBuilder("tests.shipment", 1).OnFailure(FlowFailurePolicy.Stop)
        .Repeat("track", Poll(), "check", "status", "delivered", iterations, new { parcel = "P-1" })
        .Step<Alpha>("notify", new Alpha("")).After("track").BindInput("value", "check", "track")
        .Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task A_loop_repeats_its_child_with_the_previous_output_until_the_condition_holds(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Shipment(5), new { });

        var first = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.Equal(1, first.Payload.GetProperty("iteration").GetInt32());
        Assert.True(await harness.Store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.From(new { status = "in-transit" })));
        await harness.Store.DispatchSignalsAsync();

        var second = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.NotEqual(first.WorkflowRunId, second.WorkflowRunId);
        Assert.Equal(2, second.Payload.GetProperty("iteration").GetInt32());
        Assert.Equal("in-transit", second.Payload.GetProperty("previous").GetProperty("check").GetProperty("status").GetString());
        Assert.True(await harness.Store.CompleteAsync(second.Id, second.LeaseToken!.Value, JobResult.From(new { status = "delivered" })));
        await harness.Store.DispatchSignalsAsync();

        var notify = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal("delivered", notify.Payload.GetProperty("value").GetProperty("status").GetString());
        Assert.Equal(2, (await harness.Store.ChildRunsAsync(run)).Count);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_loop_that_never_meets_its_condition_fails_after_its_iterations(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Shipment(2), new { });
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var check = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
            Assert.True(await harness.Store.CompleteAsync(check.Id, check.LeaseToken!.Value, JobResult.From(new { status = "lost" })));
            await harness.Store.DispatchSignalsAsync();
        }

        Assert.Null(await harness.Store.ClaimAsync(Claim<Beta>()));
        var finished = (await harness.Store.GetWorkflowAsync(run))!;
        Assert.Equal(FlowStatus.Failed, finished.Status);
        Assert.Contains("2 iterations", (await harness.Store.ListFlowJobsAsync(run)).Single(job => job.StepId == "track").Error);
    }

    [Fact]
    public void A_loop_needs_one_to_a_thousand_iterations()
    {
        Assert.Throws<ArgumentException>(() => Shipment(0));
        Assert.Throws<ArgumentException>(() => Shipment(FlowLoop.Limit + 1));
        Assert.Equal(EngineProtocol.Loops, Shipment(3).RequiredProtocol);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_loop_waits_for_the_protocol_that_older_workers_lack(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.Compensation}");

        await Assert.ThrowsAsync<StateConflictException>(() => new WorkflowClient(harness.Store).StartAsync(Shipment(3), new { }));
    }
}
