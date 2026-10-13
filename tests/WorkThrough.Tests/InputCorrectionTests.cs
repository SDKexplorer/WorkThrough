using System.Text.Json;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>Operators replacing the input of failed work and running it again.</summary>
public sealed class InputCorrectionTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static AdministrativeCommand Command(Guid? id = null) => new(id ?? Guid.NewGuid(), new("tests", "operator"), "Fix the customer number");

    private static ClaimRequest Claim<T>() => new("worker", [JobContractId.For<T>()], ["default"], TimeSpan.FromMinutes(1));

    private static JsonElement Input(string value) => JsonSerializer.SerializeToElement(new Alpha(value), JobJson.Options);

    [Theory, MemberData(nameof(Providers))]
    public async Task A_rejected_job_runs_again_with_the_corrected_input_and_keeps_the_original(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("bad"));
        var first = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.True(await harness.Store.RejectAsync(first.Id, first.LeaseToken!.Value, new("Rejected", "unknown customer", null)));

        var command = Command();
        await harness.Store.CorrectInputAsync(id, first.Attempt, Input("good"), command).SucceedsAsync();
        await harness.Store.CorrectInputAsync(id, first.Attempt, Input("good"), command).SucceedsAsync();
        await harness.Store.CorrectInputAsync(id, first.Attempt, Input("other"), command).FailsAsync(ErrorType.Conflict);

        var corrected = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal(("good", "bad", 2), (corrected.Payload.GetProperty("value").GetString(), corrected.OriginalPayload!.Value.GetProperty("value").GetString(),
            corrected.Attempt));
        var audit = Assert.Single(await harness.Store.AuditAsync(), entry => entry.Action == AuditActions.CorrectInput);
        Assert.DoesNotContain("good", audit.Changes);
        Assert.DoesNotContain("bad", audit.Changes);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_step_in_an_incident_resumes_its_flow_with_the_corrected_input(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("tests.correction", 1).Step<Alpha>("charge", new Alpha("bad")).Step<Beta>("ship", new Beta("ship")).After("charge").Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, new { });
        var charge = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.True(await harness.Store.RejectAsync(charge.Id, charge.LeaseToken!.Value, new("Rejected", "amount must be positive", null)));
        Assert.Equal(FlowStatus.Incident, (await harness.Store.GetWorkflowAsync(run))!.Status);

        await harness.Store.CorrectInputAsync(charge.Id, charge.Attempt, Input("good"), Command()).SucceedsAsync();
        Assert.Equal(FlowStatus.Running, (await harness.Store.GetWorkflowAsync(run))!.Status);
        var retried = (await harness.Store.ClaimAsync(Claim<Alpha>()))!;
        Assert.Equal("good", retried.Payload.GetProperty("value").GetString());
        Assert.True(await harness.Store.CompleteAsync(retried.Id, retried.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal("ship", (await harness.Store.ClaimAsync(Claim<Beta>()))!.StepId);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Only_unsuccessful_work_outside_a_running_flow_can_be_corrected(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var queued = await new JobClient(harness.Store).EnqueueAsync(new Alpha("queued"));
        await harness.Store.CorrectInputAsync(queued, 0, Input("x"), Command()).FailsAsync(ErrorType.Conflict);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.CorrectInputAsync(queued, 0, default, Command()));

        // A flow that stopped on failure is finished; its steps are not recoverable.
        var flow = new FlowBuilder("tests.correction-stop", 1).OnFailure(FlowFailurePolicy.Stop).Step<Beta>("only", new Beta("bad")).Build();
        await new WorkflowClient(harness.Store).StartAsync(flow, new { });
        var step = (await harness.Store.ClaimAsync(Claim<Beta>()))!;
        Assert.True(await harness.Store.RejectAsync(step.Id, step.LeaseToken!.Value, new("Rejected", "bad", null)));
        await harness.Store.CorrectInputAsync(step.Id, step.Attempt, Input("x"), Command()).FailsAsync(ErrorType.Conflict);
    }
}
