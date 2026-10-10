using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ExecutionInspectionTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static readonly JobDefinition<string> Invoice = new("inspection.invoice");
    private static readonly OperationActor Operator = new("tests", "operator");

    [Theory, MemberData(nameof(Providers))]
    public async Task Selected_attempt_diagnostics_are_bounded_and_do_not_deserialize_job_payloads(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(Invoice, "SECRET-INPUT");
        var pending = await store.ReadDiagnosticAsync(id, 1);
        Assert.True(pending.ExecutionExists);
        Assert.False(pending.AttemptExists);
        var attempt = (await store.ClaimAsync(new("worker", [Invoice.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await store.FailWithDetailsAsync(id, attempt.LeaseToken!.Value, new("InvoiceFailure", "Selected attempt", new string('s', 4000)), TimeSpan.Zero);
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload='SECRET-INVALID-JSON',output='SECRET-INVALID-JSON',error='SECRET-JOB-ERROR' WHERE id=@id", ("id", id.ToString()));
        var selected = await store.ReadDiagnosticAsync(id, 1, 32);
        Assert.True(selected.ExecutionExists);
        Assert.True(selected.AttemptExists);
        Assert.Equal("Selected attempt", selected.Failure!.Message);
        Assert.Equal(32, selected.Failure.StackTrace!.Length);
        Assert.True(selected.TextTruncated);
        Assert.Null((await store.ReadDiagnosticAsync(id, 2)).Failure);
        Assert.False((await store.ReadDiagnosticAsync(Guid.NewGuid(), 1)).ExecutionExists);
        var json = JsonSerializer.Serialize(selected);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain(attempt.LeaseToken.Value.ToString(), json);
        await ExecuteAsync(harness, "DELETE FROM dj_failures WHERE job_id=@id", ("id", id.ToString()));
        await ExecuteAsync(harness, "UPDATE dj_attempts SET error='x' WHERE job_id=@id", ("id", id.ToString()));
        var legacy = await store.ReadDiagnosticAsync(id, 1, 1);
        Assert.Equal("U", legacy.Failure!.ExceptionType);
        Assert.Equal("x", legacy.Failure.Message);
        Assert.True(legacy.TextTruncated);
        Assert.False((await store.ReadDiagnosticAsync(id, 2, 1)).TextTruncated);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Flow_pages_use_pinned_edges_and_job_ids_without_loading_templates_or_inputs(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("inspection.flow", 1)
            .Step("first", Invoice, "SECRET-TEMPLATE")
            .Step("second", Invoice, "SECRET-TEMPLATE-2").After("first").WhenOutput("first", "SECRET-PROPERTY", "SECRET-CONDITION")
            .Step("third", Invoice, "SECRET-TEMPLATE-3").After("first", "second").Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, "SECRET-FLOW-INPUT");
        await harness.Store.SaveDraftAsync(new FlowBuilder(flow.Key, 2).Step("different", Invoice, "input").Build(), 0, Command());
        await harness.Store.PublishAsync(flow.Key, 2, 1, Command());
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload='SECRET-INVALID-JSON',output='SECRET-INVALID-JSON' WHERE workflow_run_id=@run", ("run", run.ToString()));
        await ExecuteAsync(harness, "UPDATE dj_flows SET input='SECRET-INVALID-JSON',error='SECRET-FLOW-ERROR' WHERE id=@run", ("run", run.ToString()));
        var nodes = new List<ExecutionMetadata>();
        var edges = new List<FlowDependency>();
        var events = new List<ExecutionEvent>();
        int? nodeOffset = 0;
        int? edgeOffset = 0;
        long? afterEvent = 0;
        while (nodeOffset is not null || edgeOffset is not null || afterEvent is not null)
        {
            var page = await harness.NewStore().ReadFlowAsync(run, nodeOffset ?? 500, edgeOffset ?? 250000, afterEvent ?? long.MaxValue, 1);
            Assert.Equal(1, page.Flow!.Version);
            Assert.Equal("available", page.GraphStatus);
            Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(page));
            nodes.AddRange(page.Nodes);
            edges.AddRange(page.Dependencies);
            events.AddRange(page.Events);
            nodeOffset = page.NextNodeOffset;
            edgeOffset = page.NextDependencyOffset;
            afterEvent = page.NextAfterEvent;
            if (afterEvent is null)
            {
                // Continuing another section should resume events from the last observed store position.
                afterEvent = nodeOffset is not null || edgeOffset is not null ? page.LastEventSequence : null;
            }
        }

        Assert.Equal(3, nodes.Select(node => node.Id).Distinct().Count());
        var ids = nodes.ToDictionary(node => node.StepId!, node => node.Id);
        Assert.Equal(3, edges.Count);
        Assert.Contains(new(ids["first"], ids["second"]), edges);
        Assert.Contains(new(ids["first"], ids["third"]), edges);
        Assert.Contains(new(ids["second"], ids["third"]), edges);
        Assert.Equal(events.Count, events.Select(entry => entry.Sequence).Distinct().Count());
        Assert.NotEmpty(events);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Large_definition_reports_missing_graph_coverage_but_retains_node_metadata(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var flow = new FlowBuilder("inspection.large", 1).Step("large", Invoice, new string('x', 270000)).Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(flow, "input");
        var result = await harness.Store.ReadFlowAsync(run);
        Assert.Equal("definition_too_large", result.GraphStatus);
        Assert.Single(result.Nodes);
        Assert.Empty(result.Dependencies);
        Assert.Null(result.NextDependencyOffset);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Archived_and_trimmed_journals_report_their_observation_boundary(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var run = await new WorkflowClient(store).StartAsync(new FlowBuilder("inspection.archived", 1).Step("one", Invoice, "input").Build(), "input");
        var job = (await store.ClaimAsync(new("worker", [Invoice.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await store.CompleteAsync(job.Id, job.LeaseToken!.Value, JobResult.Done());
        await store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), Command());
        var archived = await store.ReadFlowAsync(run);
        Assert.Null(archived.Flow);
        Assert.Equal("not_live", archived.GraphStatus);
        Assert.Empty(archived.Nodes);
        Assert.NotEmpty(archived.Events);
        Assert.False((await store.ReadDiagnosticAsync(job.Id, 1)).ExecutionExists);
        await store.TrimEventsAsync(archived.LastEventSequence, Command());
        var trimmed = await store.ReadFlowAsync(run);
        Assert.True(trimmed.RequestedRangeTrimmed);
        Assert.Equal(archived.LastEventSequence, trimmed.RetainedAfterEvent);
        Assert.Empty(trimmed.Events);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Invalid_positions_limits_and_cancellation_fail_without_broadening_the_read(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadDiagnosticAsync(id, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadDiagnosticAsync(id, 1, 4097));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadFlowAsync(id, nodeOffset: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadFlowAsync(id, dependencyOffset: 250001));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadFlowAsync(id, afterEvent: long.MaxValue));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadFlowAsync(id, limit: 101));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.ReadFlowAsync(id, cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.ReadDiagnosticAsync(id, 1, cancellationToken: cancelled.Token));
        Assert.Empty(await harness.Store.AuditAsync());
    }

    private static AdministrativeCommand Command() => new(Guid.NewGuid(), Operator, "Inspection fixture");

    private static Task ExecuteAsync(StoreHarness harness, string sql, params (string Name, object Value)[] values) =>
        harness.ExecuteAsync(sql, values);
}
