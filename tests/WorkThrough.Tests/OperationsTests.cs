using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace WorkThrough.Tests;

public sealed class OperationsTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator-123", "Operator"), "Resolve incident after investigation");
    private static ClaimRequest Claim() => new("worker", [JobContractId.For<Alpha>(), JobContractId.For<Beta>()], ["default"], TimeSpan.FromSeconds(30));

    [Theory, MemberData(nameof(Providers))]
    public async Task Audit_failure_rolls_back_the_mutation_and_command_receipt(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(new Alpha("fail audit"), new(MaxAttempts: 1));
        var job = (await store.ClaimAsync(Claim()))!;
        await store.FailAsync(id, job.LeaseToken!.Value, "failure", TimeSpan.Zero);
        await using DbConnection connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = provider == "oracle" ? "CREATE TRIGGER reject_audit BEFORE INSERT ON dj_audit BEGIN raise_application_error(-20001, 'audit unavailable'); END;" : provider == "sqlserver" ? "CREATE TRIGGER reject_audit ON dj_audit AFTER INSERT AS THROW 51000,'audit unavailable',1;" : provider == "sqlite"
            ? "CREATE TRIGGER reject_audit BEFORE INSERT ON dj_audit BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END"
            : "CREATE FUNCTION reject_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit unavailable'; END $$; CREATE TRIGGER reject_audit BEFORE INSERT ON dj_audit FOR EACH ROW EXECUTE FUNCTION reject_audit();";
        await query.ExecuteNonQueryAsync();
        var command = Command();
        await Assert.ThrowsAnyAsync<DbException>(() => store.RetryJobAsync(id, 1, command));
        Assert.Equal(JobStatus.Failed, (await store.GetAsync(id))!.Status);
        Assert.Empty(await store.AuditAsync());
        query.CommandText = provider != "postgres" ? "DROP TRIGGER reject_audit" : "DROP TRIGGER reject_audit ON dj_audit";
        await query.ExecuteNonQueryAsync();
        Assert.True(await store.RetryJobAsync(id, 1, command));
        Assert.Single(await store.AuditAsync());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Drafts_use_revision_checks_and_publication_pins_runs(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var draft = new FlowBuilder("tests.draft", 1).Step<Alpha>("one", new Alpha("original")).Build();
        var save = Command();
        Assert.Equal(1, await store.SaveDraftAsync(draft, 0, save));
        Assert.Equal(1, await store.SaveDraftAsync(draft, 0, save));
        await Assert.ThrowsAsync<StateConflictException>(() => store.SaveDraftAsync(draft, 0, Command()));
        await Assert.ThrowsAsync<StateConflictException>(() => new WorkflowClient(store).StartAsync(draft, new { }));
        Assert.Equal(2, await store.PublishAsync(draft.Key, draft.Version, 1, Command()));
        var run = await new WorkflowClient(store).StartAsync(draft, new
        {
        });
        await Assert.ThrowsAsync<StateConflictException>(() => store.SaveDraftAsync(draft, 2, Command()));
        Assert.Equal(3, await store.RetireAsync(draft.Key, draft.Version, 2, Command()));
        await Assert.ThrowsAsync<StateConflictException>(() => new WorkflowClient(store).StartAsync(draft, new { }));
        Assert.Equal("original", (await store.GetRunDefinitionAsync(run))!.Steps[0].Input.GetProperty("value").GetString());
        var job = (await store.ClaimAsync(Claim()))!;
        Assert.True(await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done()));
        Assert.Equal("Succeeded", (await store.GetWorkflowAsync(run))!.Status);
        Assert.Equal(3, (await store.AuditAsync()).Count);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Incident_recovery_keeps_attempts_and_never_skips_blocked_dependents(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var flow = new FlowBuilder("tests.recovery", 1)
            .Step<Alpha>("one", new Alpha("original"), maxAttempts: 1)
            .Step<Beta>("two", new Beta("next")).After("one").Build();
        var run = await new WorkflowClient(store).StartAsync(flow, new
        {
        }, "incident");
        var first = (await store.ClaimAsync(Claim()))!;
        var exception = new JobFailure("RemoteFailure", "Service unavailable", "protected stack at Processor.cs:42");
        Assert.True(await store.FailWithDetailsAsync(first.Id, first.LeaseToken!.Value, exception, TimeSpan.Zero));
        Assert.Equal("Incident", (await store.GetWorkflowAsync(run))!.Status);
        var dependent = (await store.ListAsync(new("incident"))).Single(x => x.StepId == "two");
        Assert.Equal(JobStatus.Waiting, dependent.Status);
        Assert.Equal("FlowIncident", (await store.HealthAsync(dependent.Id))!.Reason);
        Assert.Null(await store.ClaimAsync(Claim()));
        var recovery = Command();
        Assert.True(await store.RecoverStepAsync(first.Id, 1, recovery));
        Assert.True(await store.RecoverStepAsync(first.Id, 1, recovery));
        await Assert.ThrowsAsync<StateConflictException>(() => store.RecoverStepAsync(dependent.Id, 0, recovery));
        var second = (await store.ClaimAsync(Claim()))!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Attempt);
        Assert.True(await store.CompleteAsync(second.Id, second.LeaseToken!.Value, JobResult.From("done")));
        Assert.Equal(dependent.Id, (await store.ClaimAsync(Claim()))!.Id);
        Assert.Equal(exception, (await store.FailuresAsync(first.Id)).Single().Failure);
        Assert.Equal(["Failed", "Succeeded"], (await store.AttemptsAsync(first.Id)).Select(x => x.Status));
        Assert.Single(await store.AuditAsync());
        Assert.Contains(await store.EventsAsync(run), x => x.Kind == "RecoveryRequested");
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Lease_renewal_does_not_hide_a_stalled_attempt_and_revocation_fences_progress(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(new Alpha("progress"));
        var job = (await store.ClaimAsync(Claim()))!;
        Assert.True(await store.BeginAttemptAsync(id, job.LeaseToken!.Value, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(20)));
        await Task.Delay(120);
        Assert.True(await store.RenewAsync(id, job.LeaseToken.Value, TimeSpan.FromSeconds(30)));
        var health = (await store.HealthAsync(id))!;
        Assert.True(health.SuspectedStalled);
        Assert.Equal("ExecutionDeadlineExceeded", health.Reason);
        Assert.True(await store.CancelJobAsync(id, 1, Command()));
        Assert.False(await store.ReportProgressAsync(id, job.LeaseToken.Value, "stale"));
        Assert.False(await store.FailWithDetailsAsync(id, job.LeaseToken.Value, new("late", "late", "late"), TimeSpan.Zero));
        Assert.Empty(await store.FailuresAsync(id));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Pause_defers_successor_admission_and_resume_preserves_completed_work(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var flow = new FlowBuilder("tests.pause", 1).Step<Alpha>("a", new Alpha("a"))
            .Step<Beta>("b", new Beta("b")).After("a").Build();
        var run = await new WorkflowClient(store).StartAsync(flow, new
        {
        });
        var first = (await store.ClaimAsync(Claim()))!;
        Assert.True(await store.SetFlowPausedAsync(run, true, Command()));
        Assert.True(await store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.Done()));
        Assert.Null(await store.ClaimAsync(Claim()));
        Assert.True(await store.SetFlowPausedAsync(run, false, Command()));
        Assert.Equal("b", (await store.ClaimAsync(Claim()))!.StepId);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Invalid_publication_leaves_the_draft_and_no_success_audit(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var draft = new FlowDefinition("tests.invalid", 1, []);
        await harness.Store.SaveDraftAsync(draft, 0, Command());
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.PublishAsync(draft.Key, 1, 1, Command()));
        Assert.Equal(DefinitionStatus.Draft, (await harness.Store.GetDefinitionAsync(draft.Key, 1))!.Status);
        Assert.Single(await harness.Store.AuditAsync());
    }
}
