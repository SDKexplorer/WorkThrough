using Xunit;

namespace WorkThrough.Tests;

[JobContract("tests.notify")]
public sealed record Notify(string Value);

/// <summary>Steps that run when a predecessor fails, handling that failure (ADR-028).</summary>
public sealed class FailureBranchTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static readonly SignalDefinition<Alpha> Approval = new("tests.branch-approval");

    private static ClaimRequest Claim<T>() => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1));

    // charge either ships or, when it fails, notifies the customer; both paths join at close.
    private static FlowDefinition Checkout() => new FlowBuilder("tests.branch", 1)
        .Step<Alpha>("charge", new Alpha("charge"))
        .Step<Beta>("ship", new Beta("ship")).After("charge")
        .Step<Notify>("apologise", new Notify("apologise")).OnFailureOf("charge")
        .Step<Alpha>("close", new Alpha("close")).After("ship", "apologise")
        .Build();

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_step_runs_its_branch_skips_its_successors_and_the_flow_continues(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Checkout(), new { });
        var charge = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.True(await harness.Store.RejectAsync(charge.Id, charge.LeaseToken!.Value, new("Rejected", "card declined", null)));

        var apologise = (await harness.Store.ClaimAsync(Claim<Notify>()))!;
        var failure = apologise.Payload.GetProperty(FlowFailure.Property);
        Assert.Equal(("apologise", "charge"), (apologise.Payload.GetProperty("value").GetString(), failure.GetProperty("step").GetString()));
        Assert.Contains("card declined", failure.GetProperty("error").GetString());
        Assert.Equal(FlowStatus.Running, (await harness.Store.GetWorkflowAsync(run))!.Status);
        Assert.True(await harness.Store.CompleteAsync(apologise.Id, apologise.LeaseToken!.Value, JobResult.Done()));

        var close = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal("close", close.StepId);
        Assert.True(await harness.Store.CompleteAsync(close.Id, close.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(run))!.Status);
        Assert.Equal(JobStatus.Skipped, (await harness.Store.ListFlowJobsAsync(run)).Single(job => job.StepId == "ship").Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_branch_is_skipped_when_its_step_succeeds(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var run = await new WorkflowClient(harness.Store).StartAsync(Checkout(), new { });
        await CompleteAsync<Alpha>(harness, "charge");
        await CompleteAsync<Beta>(harness, "ship");
        await CompleteAsync<Alpha>(harness, "close");

        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(run))!.Status);
        Assert.Equal(JobStatus.Skipped, (await harness.Store.ListFlowJobsAsync(run)).Single(job => job.StepId == "apologise").Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task An_expired_signal_wait_runs_its_branch(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.branch-timeout", 1)
            .WaitForSignal("approval", Approval, TimeSpan.FromMilliseconds(10))
            .Step<Notify>("remind", new Notify("remind")).OnFailureOf("approval")
            .Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, new { });
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await harness.Store.DispatchSignalsAsync();

        await CompleteAsync<Notify>(harness, "remind");
        Assert.Equal(FlowStatus.Succeeded, (await harness.Store.GetWorkflowAsync(run))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_branch_applies_the_failure_policy_and_branches_wait_for_their_protocol(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flows = new WorkflowClient(harness.Store);
        var run = await flows.StartAsync(Checkout(), new { });
        var charge = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.True(await harness.Store.RejectAsync(charge.Id, charge.LeaseToken!.Value, new("Rejected", "card declined", null)));
        var apologise = (await harness.Store.ClaimAsync(Claim<Notify>()))!;
        Assert.True(await harness.Store.RejectAsync(apologise.Id, apologise.LeaseToken!.Value, new("Rejected", "mail server down", null)));

        var incident = (await harness.Store.GetWorkflowAsync(run))!;
        Assert.Equal(FlowStatus.Incident, incident.Status);
        Assert.Contains("mail server down", incident.Error);

        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.PayloadProtection}");
        await Assert.ThrowsAsync<StateConflictException>(() => flows.StartAsync(Checkout(), new { }));
    }

    [Fact]
    public void A_branch_follows_only_the_step_it_handles()
    {
        var builder = new FlowBuilder("tests.branch-invalid", 1)
            .Step<Alpha>("charge", new Alpha("charge"))
            .Step<Beta>("ship", new Beta("ship"))
            .Step<Notify>("apologise", new Notify("apologise")).OnFailureOf("charge").After("charge", "ship");

        Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.Equal(EngineProtocol.FailureBranches, Checkout().RequiredProtocol);
    }

    private static async Task CompleteAsync<T>(StoreHarness harness, object output)
    {
        var job = (await harness.Store.ClaimAsync(Claim<T>()))!;
        Assert.True(await harness.Store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.From(output)));
    }
}
