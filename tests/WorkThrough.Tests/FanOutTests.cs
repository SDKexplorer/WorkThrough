using System.Text.Json;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>Flow steps that expand into one item step per element of an earlier output.</summary>
public sealed class FanOutTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Recover a fan-out item");

    private static ClaimRequest Claim<T>(int jobs = 1) => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1)) { MaximumJobs = jobs };

    private static FlowDefinition Flow(FlowFailurePolicy policy = FlowFailurePolicy.Stop) => new FlowBuilder("tests.fan-out", 1)
        .OnFailure(policy)
        .Step<Alpha>("pick", new Alpha("pick"))
        .ForEach("ship", JobContractId.For<Beta>(), "pick", "parcels", "value", new { })
        .Step<Alpha>("notify", new Alpha("notify")).After("ship").BindInput("value", "items", "ship")
        .Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task Each_element_runs_as_its_own_step_and_the_join_receives_every_output_in_order(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Flow(), new { });
        await CompleteAsync<Alpha>(harness, new { parcels = new[] { "p1", "p2", "p3" } });

        var items = await harness.Store.ClaimBatchAsync(Claim<Beta>(10));
        Assert.Equal(["ship[0]", "ship[1]", "ship[2]"], items.Select(item => item.StepId).Order());
        Assert.Equal(["p1", "p2", "p3"], items.OrderBy(item => item.StepId).Select(item => item.Payload.GetProperty("value").GetString()));
        Assert.Null(await harness.Store.ClaimAsync(Claim<Alpha>()));

        foreach (var item in items.OrderByDescending(item => item.StepId))
        {
            Assert.True(await harness.Store.CompleteAsync(item.Id, item.LeaseToken!.Value, JobResult.From(new { shipped = item.Payload.GetProperty("value").GetString() })));
        }

        var notify = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal("notify", notify.StepId);
        Assert.Equal(["p1", "p2", "p3"], notify.Payload.GetProperty("value").EnumerateArray().Select(output => output.GetProperty("shipped").GetString()));
        Assert.True(await harness.Store.CompleteAsync(notify.Id, notify.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(run))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task An_empty_array_completes_the_step_without_items(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await new WorkflowClient(harness.Store).StartAsync(Flow(), new { });
        await CompleteAsync<Alpha>(harness, new { parcels = Array.Empty<string>() });

        Assert.Null(await harness.Store.ClaimAsync(Claim<Beta>()));
        var notify = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal(0, notify.Payload.GetProperty("value").GetArrayLength());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_item_becomes_an_incident_and_recovery_resumes_the_join(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Flow(FlowFailurePolicy.Suspend), new { });
        await CompleteAsync<Alpha>(harness, new { parcels = new[] { "p1", "p2" } });
        var items = (await harness.Store.ClaimBatchAsync(Claim<Beta>(10))).OrderBy(item => item.StepId).ToArray();
        Assert.True(await harness.Store.RejectAsync(items[0].Id, items[0].LeaseToken!.Value, new("Rejected", "bad address", null)));
        Assert.True(await harness.Store.CompleteAsync(items[1].Id, items[1].LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(run))!.Status);
        Assert.Null(await harness.Store.ClaimAsync(Claim<Alpha>()));

        await harness.Store.RecoverStepAsync(items[0].Id, 1, Command()).SucceedsAsync();
        var retried = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.Equal("ship[0]", retried.StepId);
        Assert.True(await harness.Store.CompleteAsync(retried.Id, retried.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal("notify", (await harness.Store.ClaimAsync(Claim<Alpha>()))!.StepId);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task An_array_over_the_item_bound_fails_the_step(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Flow(), new { });
        await CompleteAsync<Alpha>(harness, new { parcels = Enumerable.Range(0, FlowFanOut.MaximumItems + 1).ToArray() });

        Assert.Equal(FlowStatus.Failed, (await harness.Store.GetWorkflowAsync(run))!.Status);
        Assert.Null(await harness.Store.ClaimAsync(Claim<Beta>()));
    }

    [Theory, MemberData(nameof(StorageConformanceTests.RowLockProviders), MemberType = typeof(StorageConformanceTests))]
    public async Task Items_settled_concurrently_complete_the_step_once(string provider)
    {
        const int count = 40;
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Flow(), new { });
        await CompleteAsync<Alpha>(harness, new { parcels = Enumerable.Range(0, count).Select(index => $"p{index}").ToArray() });
        var items = await harness.Store.ClaimBatchAsync(Claim<Beta>(ClaimRequest.MaximumBatch));
        Assert.Equal(count, items.Count);

        await Task.WhenAll(items.OrderBy(_ => Random.Shared.Next()).Chunk(4).Select(batch => Task.Run(async () =>
            Assert.All(await harness.NewStore().CompleteBatchAsync(
                batch.Select(item => new AttemptCompletion(item.Id, item.LeaseToken!.Value, JobResult.From(item.Payload.GetProperty("value").GetString()))).ToArray()),
                Assert.True))));

        var joins = await harness.Store.ClaimBatchAsync(Claim<Alpha>(10));
        var notify = Assert.Single(joins);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"p{index}"),
            notify.Payload.GetProperty("value").EnumerateArray().Select(output => output.GetString()));
        Assert.Equal(FlowStatus.Running, (await harness.Store.GetWorkflowAsync(run))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_fan_out_flow_waits_for_the_protocol_that_older_workers_lack(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.Baseline}");
        var flows = new WorkflowClient(harness.Store);
        await Assert.ThrowsAsync<StateConflictException>(() => flows.StartAsync(Flow(), new { }));
        await flows.StartAsync(new FlowBuilder("tests.plain", 1).Step<Alpha>("only", new Alpha("plain")).Build(), new { });

        // An older worker that is alive blocks activation; so does a lease held by a worker that never reported its level.
        await harness.Store.HeartbeatAsync(new("older", default, [], ["default"], 1, 0));
        var held = (await harness.Store.ClaimAsync(new ClaimRequest("silent", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromMinutes(1))))!;
        Assert.Equal(["older", "silent"], (await harness.Store.ActivateEngineProtocolAsync(EngineProtocol.FanOut)).Blockers);

        await harness.Store.HeartbeatAsync(new("older", default, [], ["default"], 1, 0) { Protocol = EngineProtocol.FanOut });
        Assert.Equal(["silent"], (await harness.Store.ActivateEngineProtocolAsync(EngineProtocol.FanOut)).Blockers);

        Assert.True(await harness.Store.CompleteAsync(held.Id, held.LeaseToken!.Value, JobResult.Done()));
        var state = await harness.Store.ActivateEngineProtocolAsync(EngineProtocol.FanOut);
        Assert.Equal((EngineProtocol.FanOut, 0), (state.Active, state.Blockers.Count));
        await flows.StartAsync(Flow(), new { });
    }

    private static async Task CompleteAsync<T>(StoreHarness harness, object output)
    {
        var job = (await harness.Store.ClaimAsync(Claim<T>()))!;
        Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.From(JsonSerializer.SerializeToElement(output, JobJson.Options))));
    }
}
