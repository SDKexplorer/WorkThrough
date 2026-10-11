using Xunit;

namespace WorkThrough.Tests;

/// <summary>Steps that run another flow and wait for its outcome (ADR-023).</summary>
public sealed class ChildFlowTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Recover a child step");

    private static ClaimRequest Claim<T>(int jobs = 1) => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1)) { MaximumJobs = jobs };

    private static FlowDefinition Packing(FlowFailurePolicy policy = FlowFailurePolicy.Stop) => new FlowBuilder("tests.packing", 1)
        .OnFailure(policy)
        .Step<Beta>("pack", new Beta("pack")).BindInput("value", "value")
        .Step<Beta>("label", new Beta("label")).After("pack")
        .Build();

    private static FlowDefinition Order(FlowDefinition child, FlowFailurePolicy policy = FlowFailurePolicy.Stop) => new FlowBuilder("tests.order", 1)
        .OnFailure(policy)
        .Step<Alpha>("reserve", new Alpha("reserve"))
        .Child("ship", child, new { value = "" }).After("reserve").BindInput("value", "value", "reserve")
        .Step<Alpha>("notify", new Alpha("notify")).After("ship").BindInput("value", "label", "ship")
        .Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task A_child_run_gets_the_step_input_and_its_final_outputs_complete_the_step(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var parent = await new WorkflowClient(harness.Store).StartAsync(Order(Packing()), new { });
        await CompleteAsync<Alpha>(harness, new { value = "order-1" });

        var pack = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.NotEqual(parent, pack.WorkflowRunId);
        Assert.Equal("order-1", pack.Payload.GetProperty("value").GetString());
        var child = (await harness.Store.GetWorkflowAsync(pack.WorkflowRunId!.Value))!;
        Assert.Equal((parent, "tests.packing"), (child.ParentRunId, child.FlowKey));
        Assert.Equal([child.Id], (await harness.Store.ChildRunsAsync(parent)).Select(run => run.Id));

        Assert.True(await harness.Store.CompleteAsync(pack.Id, pack.LeaseToken!.Value, JobResult.Done()));
        await CompleteAsync<Beta>(harness, "LBL-1");
        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(child.Id))!.Status);

        await harness.Store.DispatchSignalsAsync();
        var notify = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal("LBL-1", notify.Payload.GetProperty("value").GetString());
        Assert.True(await harness.Store.CompleteAsync(notify.Id, notify.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(parent))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_child_fails_its_step_and_recovery_starts_a_new_child_run(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var parent = await new WorkflowClient(harness.Store).StartAsync(Order(Packing(), FlowFailurePolicy.Suspend), new { });
        await CompleteAsync<Alpha>(harness, new { value = "order-2" });
        var pack = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.True(await harness.Store.RejectAsync(pack.Id, pack.LeaseToken!.Value, new("Rejected", "no stock", null)));
        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(pack.WorkflowRunId!.Value))!.Status);

        await harness.Store.DispatchSignalsAsync();
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(parent))!.Status);
        var ship = (await harness.Store.ListFlowJobsAsync(parent)).Single(job => job.StepId == "ship");
        Assert.Equal(JobStatus.Failed, ship.Status);

        await harness.Store.RecoverStepAsync(ship.Id, ship.Attempt, Command()).SucceedsAsync();
        var retried = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.NotEqual(pack.WorkflowRunId, retried.WorkflowRunId);
        Assert.Equal(parent, (await harness.Store.GetWorkflowAsync(retried.WorkflowRunId!.Value))!.ParentRunId);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Each_fan_out_item_can_run_its_own_child_flow(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.parcels", 1)
            .Step<Alpha>("pick", new Alpha("pick"))
            .ForEach("ship", Packing(), "pick", "parcels", "value", new { })
            .Step<Alpha>("notify", new Alpha("notify")).After("ship").BindInput("value", "items", "ship")
            .Build();
        var parent = await new WorkflowClient(harness.Store).StartAsync(flow, new { });
        await CompleteAsync<Alpha>(harness, new { parcels = new[] { "p1", "p2" } });

        foreach (var pack in await harness.Store.ClaimBatchAsync(Claim<Beta>(10)))
        {
            Assert.True(await harness.Store.CompleteAsync(pack.Id, pack.LeaseToken!.Value, JobResult.Done()));
        }

        foreach (var label in await harness.Store.ClaimBatchAsync(Claim<Beta>(10)))
        {
            var run = (await harness.Store.GetWorkflowAsync(label.WorkflowRunId!.Value))!;
            Assert.Equal(parent, run.ParentRunId);
            var input = (await harness.Store.ListFlowJobsAsync(run.Id)).Single(job => job.StepId == "pack").Payload.GetProperty("value").GetString();
            Assert.True(await harness.Store.CompleteAsync(label.Id, label.LeaseToken!.Value, JobResult.From($"label-{input}")));
        }

        await harness.Store.DispatchSignalsAsync();
        var notify = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal(["label-p1", "label-p2"], notify.Payload.GetProperty("value").EnumerateArray().Select(item => item.GetProperty("label").GetString()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_missing_child_or_runaway_nesting_fails_the_step(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flows = new WorkflowClient(harness.Store);
        var missing = await flows.StartAsync(WithoutChildDefinitions(new FlowBuilder("tests.missing", 1)
            .Child("ship", new FlowDefinition("tests.unpublished", 1, [])).Build()), new { });
        var step = Assert.Single(await harness.Store.ListFlowJobsAsync(missing));
        Assert.Equal(JobStatus.Failed, step.Status);
        Assert.Contains("not published", step.Error);

        // A flow that starts itself stops at the nesting bound, and the failure travels back to the outermost run.
        var root = await flows.StartAsync(WithoutChildDefinitions(
            new FlowBuilder("tests.recursive", 1).OnFailure(FlowFailurePolicy.Stop).Child("again", new FlowDefinition("tests.recursive", 1, [])).Build()), new { });
        for (var depth = 0; depth <= FlowChild.MaximumDepth && (await harness.Store.GetWorkflowAsync(root))!.Status == FlowStatus.Running; depth++)
        {
            await harness.Store.DispatchSignalsAsync();
        }

        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(root))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_child_flow_waits_for_the_protocol_that_older_workers_lack(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.ConcurrentLimits}");

        await Assert.ThrowsAsync<StateConflictException>(() => new WorkflowClient(harness.Store).StartAsync(Order(Packing()), new { }));
    }

    /// <summary>A definition as loaded from storage, which names its children without carrying them.</summary>
    private static FlowDefinition WithoutChildDefinitions(FlowDefinition definition) => definition with
    {
        Steps = [.. definition.Steps.Select(step => step.Child is null ? step : step with { Child = step.Child with { Definition = null } })]
    };

    private static async Task CompleteAsync<T>(StoreHarness harness, object output)
    {
        var job = (await harness.Store.ClaimAsync(Claim<T>()))!;
        Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.From(output)));
    }
}
