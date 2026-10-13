using Xunit;

namespace WorkThrough.Tests;

/// <summary>New runs that keep the work before a checkpoint step and run the rest again.</summary>
public sealed class ForkTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static AdministrativeCommand Command(Guid? id = null) => new(id ?? Guid.NewGuid(), new("tests", "operator"), "Rerun from the failed step");

    private static ClaimRequest Claim<T>() => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1));

    // reserve's output feeds ship, so a fork from charge must keep reserve's output.
    private static FlowDefinition Order() => new FlowBuilder("tests.fork", 1)
        .Step<Alpha>("reserve", new Alpha("reserve"))
        .Step<Beta>("charge", new Beta("charge")).After("reserve")
        .Step<Alpha>("ship", new Alpha("")).After("charge").BindInput("value", "value", "reserve")
        .Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task A_fork_keeps_earlier_outcomes_and_runs_the_checkpoint_and_its_descendants(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var source = await new WorkflowClient(harness.Store).StartAsync(Order(), new { });
        var reserve = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.True(await harness.Store.CompleteAsync(reserve.Id, reserve.LeaseToken!.Value, JobResult.From(new { value = "R-1" })));
        var charge = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.True(await harness.Store.RejectAsync(charge.Id, charge.LeaseToken!.Value, new("Rejected", "card declined", null)));
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(source))!.Status);

        var command = Command();
        var fork = await harness.Store.ForkRunAsync(source, "charge", command).SucceedsAsync();
        Assert.Equal(fork, await harness.Store.ForkRunAsync(source, "charge", command).SucceedsAsync());

        Assert.Equal(source, (await harness.Store.GetWorkflowAsync(fork))!.ForkedFromRunId);
        var kept = (await harness.Store.ListFlowJobsAsync(fork)).Single(job => job.StepId == "reserve");
        Assert.Equal((JobStatus.Succeeded, "R-1"), (kept.Status, kept.Output!.Value.GetProperty("value").GetString()));
        Assert.Empty(await harness.Store.AttemptsAsync(kept.Id));

        var rerun = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.Equal(fork, rerun.WorkflowRunId);
        Assert.True(await harness.Store.CompleteAsync(rerun.Id, rerun.LeaseToken!.Value, JobResult.Done()));
        var ship = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal(("ship", "R-1"), (ship.StepId, ship.Payload.GetProperty("value").GetString()));
        Assert.True(await harness.Store.CompleteAsync(ship.Id, ship.LeaseToken!.Value, JobResult.Done()));

        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(fork))!.Status);
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(source))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Running_flows_and_unknown_steps_cannot_be_forked(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var running = await new WorkflowClient(harness.Store).StartAsync(Order(), new { });

        await harness.Store.ForkRunAsync(running, "charge", Command()).FailsAsync(ErrorType.Conflict);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.ForkRunAsync(running, " ", Command()));

        var reserve = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.True(await harness.Store.RejectAsync(reserve.Id, reserve.LeaseToken!.Value, new("Rejected", "no stock", null)));
        await harness.Store.ForkRunAsync(running, "missing", Command()).FailsAsync(ErrorType.Conflict);
    }
}
