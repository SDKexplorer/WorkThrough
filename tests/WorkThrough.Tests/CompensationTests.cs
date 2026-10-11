using Xunit;

namespace WorkThrough.Tests;

[JobContract("tests.undo")]
public sealed record Undo(string Value);

/// <summary>Flows that undo completed steps in reverse order when they fail.</summary>
public sealed class CompensationTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Recover a compensation");

    private static ClaimRequest Claim<T>() => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1));

    // reserve and charge can be undone; ship fails.
    private static FlowDefinition Checkout() => new FlowBuilder("tests.saga", 1)
        .OnFailure(FlowFailurePolicy.Compensate)
        .Step<Alpha>("reserve", new Alpha("reserve")).CompensateWith(JobContractId.For<Undo>())
        .Step<Alpha>("charge", new Alpha("charge")).After("reserve").CompensateWith(JobContractId.For<Undo>())
        .Step<Beta>("ship", new Beta("ship")).After("charge")
        .Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_flow_undoes_completed_steps_in_reverse_order_then_fails(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Checkout(), new { });
        await CompleteAsync<Alpha>(harness, "reserved-1");
        await CompleteAsync<Alpha>(harness, "charged-1");
        var ship = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.True(await harness.Store.RejectAsync(ship.Id, ship.LeaseToken!.Value, new("Rejected", "address unknown", null)));

        var refund = (await harness.Store.ClaimAsync(Claim<Undo>()))!;
        Assert.Equal("charge:undo", refund.StepId);
        Assert.Equal(("charge", "charged-1"), (refund.Payload.GetProperty("input").GetProperty("value").GetString(), refund.Payload.GetProperty("output").GetString()));
        Assert.Null(await harness.Store.ClaimAsync(Claim<Undo>()));
        Assert.Equal(FlowStatus.Running, (await harness.Store.GetWorkflowAsync(run))!.Status);

        Assert.True(await harness.Store.CompleteAsync(refund.Id, refund.LeaseToken!.Value, JobResult.Done()));
        var release = (await harness.Store.ClaimAsync(Claim<Undo>()))!;
        Assert.Equal("reserve:undo", release.StepId);
        Assert.True(await harness.Store.CompleteAsync(release.Id, release.LeaseToken!.Value, JobResult.Done()));

        var finished = (await harness.Store.GetWorkflowAsync(run))!;
        Assert.Equal(FlowStatus.Failed, finished.Status);
        Assert.Contains("address unknown", finished.Error);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_compensation_raises_an_incident_and_recovery_finishes_undoing(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Checkout(), new { });
        await CompleteAsync<Alpha>(harness, "reserved-2");
        await CompleteAsync<Alpha>(harness, "charged-2");
        var ship = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.True(await harness.Store.RejectAsync(ship.Id, ship.LeaseToken!.Value, new("Rejected", "address unknown", null)));
        var refund = (await harness.Store.ClaimAsync(Claim<Undo>()))!;
        Assert.True(await harness.Store.RejectAsync(refund.Id, refund.LeaseToken!.Value, new("Rejected", "refund declined", null)));
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(run))!.Status);

        await harness.Store.RecoverStepAsync(refund.Id, refund.Attempt, Command()).SucceedsAsync();
        await CompleteAsync<Undo>(harness, "refunded");
        await CompleteAsync<Undo>(harness, "released");
        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(run))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_stopping_flow_never_leaves_work_queued_and_compensation_waits_for_its_protocol(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flows = new WorkflowClient(harness.Store);

        // One root fails while it is planned (its binding names a missing property) in the same pass that queues the other root.
        // The second root is skipped, not left queued in a failed flow.
        var parallel = new FlowBuilder("tests.stop-race", 1).OnFailure(FlowFailurePolicy.Stop)
            .Step<Alpha>("broken", new Alpha("broken")).BindInput("value", "missing")
            .Step<Beta>("sibling", new Beta("sibling"))
            .Build();
        var run = await flows.StartAsync(parallel, new { });
        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(run))!.Status);
        Assert.Equal(JobStatus.Skipped, (await harness.Store.ListFlowJobsAsync(run)).Single(job => job.StepId == "sibling").Status);

        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.ChildFlows}");
        await Assert.ThrowsAsync<StateConflictException>(() => flows.StartAsync(Checkout(), new { }));
    }

    private static async Task CompleteAsync<T>(StoreHarness harness, object output)
    {
        var job = (await harness.Store.ClaimAsync(Claim<T>()))!;
        Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.From(output)));
    }
}
