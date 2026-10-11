using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ExecutionQueryTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static readonly JobDefinition<string> Invoice = new("queries.invoice", displayName: "Invoice processing");
    private static ClaimRequest Claim() => new("SECRET-WORKER", [Invoice.Identity], ["default"], TimeSpan.FromMinutes(1));

    [Theory, MemberData(nameof(Providers))]
    public async Task Metadata_queries_do_not_load_sensitive_json_or_implementation_fields(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(Invoice, "SECRET-INPUT", new(IdempotencyKey: "SECRET-KEY"));
        var job = (await store.ClaimAsync(Claim()))!;
        await store.RecordHandlerAsync(id, job.LeaseToken!.Value, "SECRET-ASSEMBLY");
        await store.FailWithDetailsAsync(id, job.LeaseToken.Value, new("SECRET-TYPE", "SECRET-MESSAGE", "SECRET-STACK"), TimeSpan.Zero);
        // Invalid JSON makes a regression to loading JobRecord fail, even if an adapter hides fields afterwards.
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload='SECRET-INVALID-JSON',output='SECRET-INVALID-JSON' WHERE id=@id", ("id", id.ToString()));
        var lookup = await store.FindExecutionAsync(id);
        var page = await store.SearchExecutionsAsync(new(ProcessorKey: Invoice.Identity.Key));
        var attempts = await store.ReadAttemptsAsync(id);
        Assert.Equal(id, lookup.Execution!.Id);
        Assert.Equal(id, Assert.Single(page.Items).Id);
        Assert.Equal(AttemptStatus.Failed, Assert.Single(attempts.Items).Status);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(new { lookup, page, attempts }));
        Assert.DoesNotContain(job.LeaseToken.Value.ToString(), JsonSerializer.Serialize(lookup));
        Assert.Equal("live", page.Coverage);
        Assert.Equal(page.StoreId, lookup.StoreId);
        Assert.Empty(await store.AuditAsync());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Keyset_pages_preserve_ties_and_window_across_connections(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expected = new List<Guid>();
        for (var i = 1; i <= 11; i++)
        {
            var record = JobClient.CreateRecord(Invoice.Identity, "input") with
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                CreatedAt = timestamp,
                UseStorageTime = false
            };
            expected.Add(await harness.Store.EnqueueAsync(record));
        }

        var query = new ExecutionQuery(Limit: 3);
        var first = await harness.Store.SearchExecutionsAsync(query);
        Assert.Equal(3, first.Items.Count);
        var ids = first.Items.Select(x => x.Id).ToList();
        var cursor = first.NextCursor;
        // Later inserts that sort before the continuation position do not shift following pages.
        await new JobClient(harness.Store).EnqueueAsync(Invoice, "newer");
        while (cursor is not null)
        {
            var page = await harness.NewStore().SearchExecutionsAsync(query with { Cursor = cursor });
            Assert.Equal(first.From, page.From);
            Assert.Equal(first.Until, page.Until);
            Assert.Equal(first.StoreId, page.StoreId);
            ids.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
        }

        Assert.Equal(expected, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Failure_window_finds_old_jobs_that_recovered_and_returns_each_job_once(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await store.EnqueueAsync(JobClient.CreateRecord(Invoice.Identity, "input") with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-30), UseStorageTime = false });
        for (var i = 0; i < 2; i++)
        {
            var attempt = (await store.ClaimAsync(Claim()))!;
            Assert.True(await store.FailAsync(id, attempt.LeaseToken!.Value, "SECRET-FAILURE", TimeSpan.Zero));
        }

        var recovered = (await store.ClaimAsync(Claim()))!;
        await store.CompleteAsync(id, recovered.LeaseToken!.Value, JobResult.Done());
        Assert.Empty((await store.SearchExecutionsAsync(new())).Items);
        var failed = await store.SearchExecutionsAsync(new(TimeBasis: ExecutionTimeBasis.FailedAttempt));
        Assert.Equal(id, Assert.Single(failed.Items).Id);
        Assert.Equal(JobStatus.Succeeded, failed.Items[0].Status);
        Assert.Empty((await store.SearchExecutionsAsync(new(Status: JobStatus.Failed, TimeBasis: ExecutionTimeBasis.FailedAttempt))).Items);
        var firstAttempts = await store.ReadAttemptsAsync(id, limit: 2);
        Assert.Equal([AttemptStatus.Failed, AttemptStatus.Failed], firstAttempts.Items.Select(x => x.Status));
        Assert.Equal(2, firstAttempts.NextAfterAttempt);
        var lastAttempt = await store.ReadAttemptsAsync(id, firstAttempts.NextAfterAttempt!.Value, 2);
        Assert.Equal(AttemptStatus.Succeeded, Assert.Single(lastAttempt.Items).Status);
        Assert.Null(lastAttempt.NextAfterAttempt);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Time_windows_are_half_open_and_preserve_fractional_boundaries(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var boundary = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());
        var first = await harness.Store.EnqueueAsync(JobClient.CreateRecord(Invoice.Identity, "first") with { CreatedAt = boundary, UseStorageTime = false });
        var second = await harness.Store.EnqueueAsync(JobClient.CreateRecord(Invoice.Identity, "second") with { CreatedAt = boundary.AddMilliseconds(1), UseStorageTime = false });
        var exact = await harness.Store.SearchExecutionsAsync(new(From: boundary, Until: boundary.AddMilliseconds(1)));
        Assert.Equal(first, Assert.Single(exact.Items).Id);
        var fractional = await harness.Store.SearchExecutionsAsync(new(From: boundary.AddTicks(1), Until: boundary.AddMilliseconds(2)));
        Assert.Equal(second, Assert.Single(fractional.Items).Id);
        var fractionalUpper = await harness.Store.SearchExecutionsAsync(new(From: boundary.AddMilliseconds(-1), Until: boundary.AddTicks(1)));
        Assert.Equal(first, Assert.Single(fractionalUpper.Items).Id);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Filters_preserve_exact_identity_queue_correlation_and_flow(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var processor = new JobContractId(Invoice.Identity.Key, 2);
        var client = new JobClient(harness.Store);
        var expected = await client.EnqueueAsync(processor, "input", new(Queue: "billing", CorrelationId: "order:123"));
        await client.EnqueueAsync(Invoice, "wrong-version", new(Queue: "billing", CorrelationId: "order:123"));
        await client.EnqueueAsync(processor, "wrong-queue", new(CorrelationId: "order:123"));
        await client.EnqueueAsync(processor, "wrong-correlation", new(Queue: "billing", CorrelationId: "order:124"));
        var page = await harness.Store.SearchExecutionsAsync(new("order:123", JobStatus.Queued, processor.Key, 2, "billing"));
        Assert.Equal(expected, Assert.Single(page.Items).Id);
        Assert.Empty((await harness.Store.SearchExecutionsAsync(new(CorrelationId: "' OR 1=1 --"))).Items);
        var definition = new FlowBuilder("queries.flow", 1).Step("first", Invoice.Identity, "first")
            .Step("second", Invoice.Identity, "second").After("first").Build();
        var run = await new WorkflowClient(harness.Store).StartAsync(definition, new { }, "order:123");
        var flow = await harness.Store.SearchExecutionsAsync(new(WorkflowRunId: run));
        Assert.Equal(2, flow.Items.Count);
        Assert.All(flow.Items, x => Assert.Equal(run, x.WorkflowRunId));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Cursors_reject_other_stores_changed_filters_and_malformed_input(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await using var other = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        await client.EnqueueAsync(Invoice, "first");
        await client.EnqueueAsync(Invoice, "second");
        var query = new ExecutionQuery(Limit: 1);
        var cursor = (await harness.Store.SearchExecutionsAsync(query)).NextCursor;
        Assert.NotNull(cursor);
        await Assert.ThrowsAsync<ArgumentException>(() => other.Store.SearchExecutionsAsync(query with { Cursor = cursor }));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchExecutionsAsync(query with { Cursor = cursor, Status = JobStatus.Failed }));
        foreach (var invalid in new[] { "", "not base64!", Convert.ToBase64String("null"u8.ToArray()), Convert.ToBase64String("{}"u8.ToArray()) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchExecutionsAsync(query with { Cursor = invalid }));
        }
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Invalid_limits_windows_enums_and_cancelled_reads_fail_explicitly(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var now = DateTimeOffset.UtcNow;
        ExecutionQuery[] invalid = [new(Limit: 0), new(Limit: 101), new(ContractVersion: 2), new(ProcessorKey: "Invalid Key"),
            new(Status: (JobStatus)999), new(TimeBasis: (ExecutionTimeBasis)999), new(From: now, Until: now),
            new(From: now.AddDays(-8), Until: now), new(Queue: new string('q', 513)), new(Cursor: new string('x', 2049))];
        foreach (var query in invalid)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Store.SearchExecutionsAsync(query));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Store.ReadAttemptsAsync(Guid.NewGuid(), afterAttempt: -1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.SearchExecutionsAsync(new(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.FindExecutionAsync(Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.ReadAttemptsAsync(Guid.NewGuid(), cancellationToken: cancellation.Token));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Long_metadata_is_explicitly_clipped_and_archived_work_is_outside_live_coverage(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await new JobClient(store).EnqueueAsync(Invoice, "input", new(CorrelationId: new string('c', 600)));
        var item = (await store.FindExecutionAsync(id)).Execution!;
        Assert.True(item.MetadataTruncated);
        Assert.Equal(512, item.CorrelationId!.Length);
        var job = (await store.ClaimAsync(Claim()))!;
        await store.CompleteAsync(id, job.LeaseToken!.Value, JobResult.Done());
        await store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), new(Guid.NewGuid(), new("tests", "queries"), "Verify history coverage")).SucceedsAsync();
        var lookup = await store.FindExecutionAsync(id);
        Assert.Null(lookup.Execution);
        Assert.Equal("live", lookup.Coverage);
        var attempts = await store.ReadAttemptsAsync(id);
        Assert.False(attempts.ExecutionExists);
        Assert.Empty(attempts.Items);
        Assert.Empty((await store.SearchExecutionsAsync(new())).Items);
        Assert.Single(await store.ArchivesAsync());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Provider_registration_exposes_the_same_read_only_port(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var services = new ServiceCollection();
        var builder = services.AddWorkThrough();
        harness.Register(builder);

        await using var serviceProvider = services.BuildServiceProvider();
        var read = serviceProvider.GetRequiredService<IExecutionQueries>();
        Assert.Equal((await harness.Store.FindExecutionAsync(Guid.NewGuid())).StoreId, (await read.SearchExecutionsAsync(new())).StoreId);
    }

    private static Task ExecuteAsync(StoreHarness harness, string sql, params (string Name, object Value)[] values) =>
        harness.ExecuteAsync(sql, values);
}
