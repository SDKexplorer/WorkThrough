using System.Data.Common;
using System.Text.Json;
using Xunit;

namespace WorkThrough.Tests;

public sealed class CompletionTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static readonly JobDefinition<Alpha> Work = new("tests.alpha");
    private static readonly SignalDefinition<Alpha> Approval = new("tests.approval");
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operations"), "Verify execution lifecycle");
    private static ClaimRequest Claim(string worker = "worker") => new(worker, [Work.Identity, JobContractId.For<Beta>()], ["default"], TimeSpan.FromSeconds(30));

    [Theory, MemberData(nameof(Providers))]
    public async Task Queue_pause_drain_expiry_rejection_deferral_and_replay_have_distinct_semantics(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var client = new JobClient(store);
        var id = await client.EnqueueAsync(Work, new Alpha("work"));
        await store.SetQueuePausedAsync("default", true, 0, Command()).SucceedsAsync();
        Assert.Null(await store.ClaimAsync(Claim()));
        await store.SetQueuePausedAsync("default", false, 1, Command()).SucceedsAsync();
        var first = (await store.ClaimAsync(Claim()))!;
        await store.SetWorkerDrainingAsync("worker", true, 0, Command()).SucceedsAsync();
        Assert.True(await store.CompleteAsync(id, first.LeaseToken!.Value, JobResult.Defer(TimeSpan.FromMilliseconds(10), "Wait for capacity")));
        await Task.Delay(30);
        Assert.Null(await store.ClaimAsync(Claim()));
        var next = (await store.ClaimAsync(Claim("replacement")))!;
        Assert.Equal(2, next.Attempt);
        Assert.Equal(4, next.MaxAttempts);
        Assert.True(await store.CompleteAsync(id, next.LeaseToken!.Value, JobResult.Reject("Invalid business request")));
        Assert.Equal(JobStatus.Rejected, (await store.GetAsync(id))!.Status);
        Assert.Null(await store.ClaimAsync(Claim("replacement")));
        var replay = await store.ReplayAsync(id, Command()).SucceedsAsync();
        Assert.NotEqual(id, replay);
        Assert.Equal(id, (await store.GetAsync(replay))!.CausationJobId);
        await store.CancelAsync(replay);
        var expired = await client.EnqueueAsync(Work, new Alpha("obsolete"), new(ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.Null(await store.ClaimAsync(Claim("replacement")));
        Assert.Equal(JobStatus.Expired, (await store.GetAsync(expired))!.Status);
        Assert.Empty(await store.AttemptsAsync(expired));
        Assert.Equal([AttemptStatus.Deferred, AttemptStatus.Rejected], (await store.AttemptsAsync(id)).Select(a => a.Status));
        await store.SetWorkerDrainingAsync("worker", false, 1, Command()).SucceedsAsync();
        var audit = await store.AuditAsync();
        Assert.Contains(audit, entry => entry.Action == "queues.pause" && entry.Target == "default");
        Assert.Contains(audit, entry => entry.Action == "queues.resume" && entry.Target == "default");
        Assert.Contains(audit, entry => entry.Action == "workers.drain" && entry.Target == "worker");
        Assert.Contains(audit, entry => entry.Action == "workers.resume" && entry.Target == "worker");
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Signal_can_arrive_early_is_deduplicated_and_advances_without_attempts(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var client = new WorkflowClient(store);
        var definition = new FlowBuilder("tests.signals", 1).Step("start", Work, new Alpha("a"))
            .WaitForSignal("approval", Approval, TimeSpan.FromMinutes(1)).After("start")
            .Step<Beta>("finish").After("approval").BindInput("value", "value", "approval").Build();
        var run = await client.StartAsync(definition, new
        {
        });
        Assert.True(await client.SignalAsync(run, Approval, new Alpha("accepted"), "receipt"));
        var first = (await store.ClaimAsync(Claim()))!;
        await store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.Done());
        var resumed = harness.NewStore();
        var signal = Assert.Single(await resumed.ListFlowJobsAsync(run), j => j.StepId == "approval");
        Assert.Equal(JobStatus.Succeeded, signal.Status);
        Assert.Empty(await resumed.AttemptsAsync(signal.Id));
        var finish = (await resumed.ClaimAsync(Claim()))!;
        Assert.Equal("accepted", finish.Payload.GetProperty("value").GetString());
        await resumed.CompleteAsync(finish.Id, finish.LeaseToken!.Value, JobResult.Done());
        Assert.True(await client.SignalAsync(run, Approval, new Alpha("accepted"), "receipt"));
        await Assert.ThrowsAsync<StateConflictException>(() => client.SignalAsync(run, Approval, new Alpha("different"), "receipt"));
        Assert.Equal(FlowStatus.Succeeded, (await store.GetWorkflowAsync(run))!.Status);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Signal_timeout_creates_an_incident_and_can_be_explicitly_rearmed(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var definition = new FlowBuilder("tests.timeout", 1).WaitForSignal("wait", Approval, TimeSpan.FromMilliseconds(10)).Build();
        var run = await new WorkflowClient(store).StartAsync(definition, new
        {
        });
        await Task.Delay(30);
        await store.DispatchSignalsAsync();
        Assert.Equal(FlowStatus.Incident, (await store.GetWorkflowAsync(run))!.Status);
        var wait = Assert.Single(await store.ListFlowJobsAsync(run));
        Assert.Equal(JobStatus.Expired, wait.Status);
        await new WorkflowClient(store).SignalAsync(run, Approval, new Alpha("late"), "late-signal");
        Assert.Equal(JobStatus.Expired, (await store.GetAsync(wait.Id))!.Status);
        await store.RecoverStepAsync(wait.Id, 0, Command()).SucceedsAsync();
        Assert.Equal(FlowStatus.Succeeded, (await store.GetWorkflowAsync(run))!.Status);
        Assert.Empty(await store.AttemptsAsync(wait.Id));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Archive_moves_whole_terminal_flows_preserves_active_work_and_deduplication(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var jobs = new JobClient(store);
        var id = await jobs.EnqueueAsync(Work, new Alpha("archived"), new(IdempotencyKey: "receipt"));
        var claim = (await store.ClaimAsync(Claim()))!;
        await store.CompleteAsync(id, claim.LeaseToken!.Value, JobResult.Done());
        var flow = new FlowBuilder("tests.archive", 1).Step("one", Work, new Alpha("flow")).Step("two", Work, new Alpha("flow")).After("one").Build();
        var run = await new WorkflowClient(store).StartAsync(flow, new
        {
        }, null, "flow-receipt");
        for (var i = 0; i < 2; i++)
        {
            var step = (await store.ClaimAsync(Claim()))!;
            await store.CompleteAsync(step.Id, step.LeaseToken!.Value, JobResult.Done());
        }

        var active = await jobs.EnqueueAsync(Work, new Alpha("active"));
        var policy = new ArchivePolicy(DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, (await store.ArchiveCandidatesAsync(policy)).Count);
        var command = Command();
        var archived = await store.ArchiveCompletedAsync(policy, command).SucceedsAsync();
        Assert.Equal(2, archived.Count);
        Assert.Equal(archived, await store.ArchiveCompletedAsync(policy, command).SucceedsAsync());
        Assert.NotNull(await store.GetAsync(active));
        Assert.Null(await store.GetAsync(id));
        Assert.Empty(await store.ListFlowJobsAsync(run));
        var exported = (await store.ExportArchiveAsync(run))!;
        var package = JsonSerializer.Deserialize<ExecutionArchive>(exported.Json, JobJson.Options)!;
        Assert.Equal(2, package.Jobs.Count);
        Assert.All(package.Jobs, j => Assert.Single(j.Attempts));
        Assert.Equal(flow.Key, package.Definition!.Key);
        Assert.Equal(id, await jobs.EnqueueAsync(Work, new Alpha("archived"), new(IdempotencyKey: "receipt")));
        Assert.Equal(run, await new WorkflowClient(store).StartAsync(flow, new
        {
        }, null, "flow-receipt"));
        await store.PurgeArchiveAsync(run, "incorrect", Command()).FailsAsync(ErrorType.Conflict);
        Assert.True(await store.PurgeArchiveAsync(run, exported.Summary.Sha256, Command()).SucceedsAsync());
        Assert.Null(await store.ExportArchiveAsync(run));
        Assert.Equal(2, (await store.ArchivesAsync()).Count);
        Assert.Equal(run, await new WorkflowClient(store).StartAsync(flow, new
        {
        }, null, "flow-receipt"));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Business_write_and_job_or_outbox_roll_back_together(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        await harness.ExecuteAsync("CREATE TABLE business_orders(id INTEGER PRIMARY KEY)");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InTransactionAsync<bool>(async (connection, transaction, jobs, ct) =>
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO business_orders(id) VALUES (1)", ct);
            await jobs.EnqueueAsync(Work, new Alpha("direct"), cancellationToken: ct);
            await jobs.SendAsync(Work, new Alpha("outbox"), cancellationToken: ct);
            throw new InvalidOperationException("Rollback");
        }));
        Assert.Empty(await store.ListAsync(new()));
        await using var destination = await StoreHarness.CreateAsync(provider);
        Assert.Equal(0, await store.DispatchOutboxAsync(destination.Store));
        var receipt = await store.InTransactionAsync(async (connection, transaction, jobs, ct) =>
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO business_orders(id) VALUES (1)", ct);

            return await jobs.SendAsync(Work, new Alpha("committed"), new(IdempotencyKey: "business-order-1"), ct);
        });
        Assert.Equal(1, await store.DispatchOutboxAsync(destination.Store));
        Assert.Equal(0, await store.DispatchOutboxAsync(destination.Store));
        Assert.Equal("Delivered", (await store.OutboxDeliveryAsync(receipt))!.Status);
        Assert.Single(await destination.Store.ListAsync(new()));
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
