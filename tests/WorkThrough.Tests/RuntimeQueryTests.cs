using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class RuntimeQueryTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static readonly JobDefinition<string> Invoice = new("runtime.invoice");
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "runtime fixture");
    private static ClaimRequest Claim() => new("worker", [Invoice.Identity], ["default"], TimeSpan.FromMinutes(1));

    [Theory, MemberData(nameof(Providers))]
    public async Task Readiness_matches_processor_and_queue_on_same_worker_and_reports_draining_health_and_capacity(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(Invoice, "SECRET-INPUT");
        await store.HeartbeatAsync(new("processor-only", default, [Invoice.Identity], ["other"], 2, 0));
        await store.HeartbeatAsync(new("queue-only", default, [new("other", 1)], ["default"], 2, 0));
        Assert.Equal("NoRecentCapableWorker", (await store.ReadReadinessAsync(id)).Reason);
        var worker = new WorkerHeartbeat("capable", default, [Invoice.Identity], ["default"], 2, 0);
        await store.HeartbeatAsync(worker);
        Assert.Equal("AwaitingAcquisition", (await store.ReadReadinessAsync(id)).Reason);
        await store.SetWorkerDrainingAsync(worker.WorkerId, true, 0, Command());
        Assert.Equal("WorkersDraining", (await store.ReadReadinessAsync(id)).Reason);
        await store.SetWorkerDrainingAsync(worker.WorkerId, false, 1, Command());
        await store.HeartbeatAsync(worker with { Acquisition = new(default, default, 3) });
        Assert.Equal("AcquisitionFailing", (await store.ReadReadinessAsync(id)).Reason);
        await store.HeartbeatAsync(worker with { Running = 2 });
        Assert.Equal("WorkerCapacity", (await store.ReadReadinessAsync(id)).Reason);
        await store.SetLimitAsync(new(null, 0));
        var stopped = await store.ReadReadinessAsync(id);
        Assert.Equal("SharedCapacity", stopped.Reason);
        Assert.Equal(0, stopped.Capacity!.GlobalMaximum);
        await store.SetQueuePausedAsync("default", true, 0, Command());
        Assert.Equal("QueuePaused", (await store.ReadReadinessAsync(id)).Reason);
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload='SECRET-INVALID-JSON',output='SECRET-INVALID-JSON'");
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(await store.ReadReadinessAsync(id)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Incomplete_worker_observations_never_claim_absent_capacity_and_expired_leases_do_not_count(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(Invoice, "input");
        await store.HeartbeatAsync(new("oversized", default, [Invoice.Identity], ["default"], 1, 0));
        await ExecuteAsync(harness, "UPDATE dj_workers SET details=@details", ("details", new string('x', 262145)));
        var unknown = await store.ReadReadinessAsync(id);
        Assert.True(unknown.WorkerCoverageIncomplete);
        Assert.Equal("WorkerCoverageIncomplete", unknown.Reason);
        await ExecuteAsync(harness, "UPDATE dj_workers SET seen_at=0");
        Assert.Equal("NoRecentCapableWorker", (await store.ReadReadinessAsync(id)).Reason);
        var job = (await store.ClaimAsync(Claim()))!;
        await store.SetLimitAsync(new(null, 1));
        Assert.Equal(1, (await store.ReadReadinessAsync(id)).Capacity!.GlobalAdmitted);
        await ExecuteAsync(harness, "UPDATE dj_jobs SET lease_expires_at=0 WHERE id=@id", ("id", id.ToString()));
        var expired = await store.ReadReadinessAsync(id);
        Assert.Equal("LeaseExpired", expired.Reason);
        Assert.Equal(0, expired.Capacity!.GlobalAdmitted);
        Assert.DoesNotContain(job.LeaseToken!.Value.ToString(), JsonSerializer.Serialize(expired));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Readiness_covers_delays_progress_incidents_and_stable_saved_step_labels(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var delayed = await new JobClient(store).EnqueueAsync(Invoice, "input", new(NotBefore: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("Scheduled", (await store.ReadReadinessAsync(delayed)).Reason);
        var flow = new FlowBuilder("runtime.flow", 1).Step("first", Invoice, "input").Named("Validate invoice")
            .Step("second", Invoice, "input").Named("Send receipt").After("first").Build();
        var run = await new WorkflowClient(store).StartAsync(flow, "input");
        var jobs = await store.ListFlowJobsAsync(run);
        var waiting = jobs.Single(job => job.StepId == "second");
        Assert.Equal("Dependencies", (await store.ReadReadinessAsync(waiting.Id)).Reason);
        await store.SetFlowPausedAsync(run, true, Command());
        Assert.Equal("FlowPaused", (await store.ReadReadinessAsync(waiting.Id)).Reason);
        await store.SetFlowPausedAsync(run, false, Command());
        var active = (await store.ClaimAsync(Claim()))!;
        await store.BeginAttemptAsync(active.Id, active.LeaseToken!.Value, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        await ExecuteAsync(harness, "UPDATE dj_progress SET last_progress_at=0,description='SECRET-PROGRESS'");
        Assert.Equal("ProgressOverdue", (await store.ReadReadinessAsync(active.Id)).Reason);
        await ExecuteAsync(harness, "UPDATE dj_progress SET deadline=0");
        var overdue = await store.ReadReadinessAsync(active.Id);
        Assert.Equal("ExecutionDeadlineExceeded", overdue.Reason);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(overdue));
        await store.RejectAsync(active.Id, active.LeaseToken.Value, new("Error", "failure", null));
        Assert.Equal("FlowIncident", (await store.ReadReadinessAsync(waiting.Id)).Reason);
        var saved = await harness.NewStore().GetRunDefinitionAsync(run);
        Assert.Equal("Validate invoice", saved!.Steps[0].DisplayName);
        Assert.Equal("first", saved.Steps[0].Id);
        Assert.Throws<ArgumentException>(() => new FlowBuilder("invalid.label", 1).Step("one", Invoice, "input").Named(new string('x', 201)).Build());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Windowed_attempt_metrics_use_completion_time_and_explicit_archive_coverage(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var empty = await store.ReadRuntimeMetricsAsync(new());
        Assert.Equal(0, empty.FinishedAttempts);
        Assert.Null(empty.MeanDurationMilliseconds);
        var id = await new JobClient(store).EnqueueAsync(Invoice, "input");
        var failed = (await store.ClaimAsync(Claim()))!;
        await store.FailAsync(id, failed.LeaseToken!.Value, "SECRET", TimeSpan.Zero);
        var recovered = (await store.ClaimAsync(Claim()))!;
        await store.CompleteAsync(id, recovered.LeaseToken!.Value, JobResult.Done());
        var boundary = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        await ExecuteAsync(harness, "UPDATE dj_attempts SET started_at=@start,finished_at=@end", ("start", boundary.AddMilliseconds(-100).ToUnixTimeMilliseconds()), ("end", boundary.ToUnixTimeMilliseconds()));
        var result = await store.ReadRuntimeMetricsAsync(new(boundary, boundary.AddSeconds(1)));
        Assert.Equal(2, result.FinishedAttempts);
        Assert.Equal(1, result.SucceededAttempts);
        Assert.Equal(1, result.FailedAttempts);
        Assert.Equal(100, result.MeanDurationMilliseconds);
        Assert.Equal(100, result.MaximumDurationMilliseconds);
        Assert.Equal(1, result.SuccessfulAttemptsPerSecond);
        Assert.Equal(0, (await store.ReadRuntimeMetricsAsync(new(boundary.AddTicks(1), boundary.AddSeconds(1)))).FinishedAttempts);
        await store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), Command());
        Assert.Equal(0, (await store.ReadRuntimeMetricsAsync(new())).FinishedAttempts);
        Assert.Equal("live-attempts", result.Coverage);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadReadinessAsync(id, 101));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadRuntimeMetricsAsync(new(From: DateTimeOffset.UtcNow.AddDays(-8))));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadRuntimeMetricsAsync(new(), cancelled.Token));
        Assert.False((await store.ReadReadinessAsync(id)).ExecutionExists);
    }

    private static async Task ExecuteAsync(StoreHarness harness, string sql, params (string Name, object Value)[] values)
    {
        await using DbConnection connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
