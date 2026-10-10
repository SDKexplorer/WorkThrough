using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class OperationHistoryTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    private static readonly JobDefinition<string> Invoice = new("history.invoice");
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("test-issuer", "operator", "SECRET-DISPLAY"), "SECRET-REASON");

    [Theory, MemberData(nameof(Providers))]
    public async Task Audit_pages_preserve_ties_and_project_attribution_without_sensitive_evidence(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var commands = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var command = Command();
            commands.Add(command.Id);
            await harness.Store.SetQueuePausedAsync($"queue-{i}", true, 0, command).SucceedsAsync();
        }

        var boundary = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        await ExecuteAsync(harness, "UPDATE dj_audit SET at=@at", ("at", boundary.ToUnixTimeMilliseconds()));
        await ExecuteAsync(harness, "UPDATE dj_audit_changes SET changes='SECRET-INVALID-JSON'");
        var query = new AuditQuery(Action: "queues.pause", Limit: 2);
        var first = await harness.Store.SearchAuditAsync(query);
        var items = first.Items.ToList();
        var cursor = first.NextCursor;
        await harness.Store.SetQueuePausedAsync("newer", true, 0, Command()).SucceedsAsync();
        while (cursor is not null)
        {
            var page = await harness.NewStore().SearchAuditAsync(query with { Cursor = cursor });
            Assert.Equal(first.From, page.From);
            Assert.Equal(first.Until, page.Until);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }

        Assert.Equal(commands.OrderBy(id => id.ToString(), StringComparer.Ordinal), items.Select(item => item.CommandId));
        Assert.All(items, item =>
        {
            Assert.True(item.ActorAvailable);
            Assert.Equal("test-issuer", item.Issuer);
            Assert.Equal("operator", item.Subject);
        });
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(items));
        Assert.Single((await harness.Store.SearchAuditAsync(new(Target: "queue-2"))).Items);
        Assert.Empty((await harness.Store.SearchAuditAsync(new(Target: "' OR 1=1 --"))).Items);
        Assert.Empty((await harness.Store.SearchAuditAsync(new(From: boundary.AddTicks(1), Until: boundary.AddMilliseconds(2)))).Items);
        Assert.Equal(5, (await harness.Store.SearchAuditAsync(new(From: boundary, Until: boundary.AddTicks(1)))).Items.Count);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Actor_bounds_report_unavailable_attribution_without_loading_huge_actor_json(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.SetQueuePausedAsync("long-issuer", true, 0, Command() with { Actor = new(new string('i', 600), "operator") }).SucceedsAsync();
        var clipped = Assert.Single((await harness.Store.SearchAuditAsync(new())).Items);
        Assert.Equal(512, clipped.Issuer!.Length);
        Assert.True(clipped.MetadataTruncated);
        await harness.Store.SetQueuePausedAsync("large-actor", true, 0, Command() with { Actor = new("issuer", "operator", new string('d', 17000)) }).SucceedsAsync();
        var unavailable = Assert.Single((await harness.Store.SearchAuditAsync(new(Target: "large-actor"))).Items);
        Assert.False(unavailable.ActorAvailable);
        Assert.True(unavailable.MetadataTruncated);
        Assert.Null(unavailable.Issuer);
        Assert.Null(unavailable.Subject);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Archive_summaries_page_without_body_reads_and_preserve_purged_and_old_coverage(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await CompleteAsync(harness, "same"));
        }

        await harness.Store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), Command()).SucceedsAsync();
        var summaries = await harness.Store.ArchivesAsync();
        await harness.Store.PurgeArchiveAsync(ids[0], summaries.Single(item => item.Id == ids[0]).Sha256, Command()).SucceedsAsync();
        await ExecuteAsync(harness, "UPDATE dj_archives SET body='SECRET-INVALID-JSON' WHERE body IS NOT NULL");
        var boundary = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        await ExecuteAsync(harness, "UPDATE dj_archives SET archived_at=@at", ("at", boundary.ToUnixTimeMilliseconds()));
        var query = new ArchiveQuery(CorrelationId: "same", Kind: ArchiveKind.Job, Limit: 1);
        var page = await harness.Store.SearchArchivesAsync(query);
        var items = page.Items.ToList();
        var cursor = page.NextCursor;
        while (cursor is not null)
        {
            var next = await harness.NewStore().SearchArchivesAsync(query with { Cursor = cursor });
            Assert.Equal(page.From, next.From);
            Assert.Equal(page.Until, next.Until);
            items.AddRange(next.Items);
            cursor = next.NextCursor;
        }

        Assert.Equal(ids.OrderBy(id => id.ToString(), StringComparer.Ordinal), items.Select(item => item.Id));
        Assert.False(items.Single(item => item.Id == ids[0]).BodyAvailable);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(items));
        Assert.Equal("archive-summary", page.Coverage);
        Assert.Empty((await harness.Store.SearchArchivesAsync(new(Kind: ArchiveKind.Flow))).Items);
        Assert.Empty((await harness.Store.SearchArchivesAsync(new(From: boundary.AddTicks(1), Until: boundary.AddMilliseconds(2)))).Items);
        await ExecuteAsync(harness, "UPDATE dj_archives SET archived_at=@at", ("at", boundary.AddDays(-30).ToUnixTimeMilliseconds()));
        Assert.Empty((await harness.Store.SearchArchivesAsync(new())).Items);
        Assert.Equal(ids[0], (await harness.Store.FindArchiveAsync(ids[0])).Archive!.Id);
        Assert.Null((await harness.Store.FindArchiveAsync(Guid.NewGuid())).Archive);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Retention_preview_matches_manual_selection_without_mutation_or_payload_reads(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var first = await CompleteAsync(harness, new string('c', 600));
        var second = await CompleteAsync(harness, "same");
        var failed = await new JobClient(store).EnqueueAsync(Invoice, "input");
        var attempt = (await store.ClaimAsync(Claim()))!;
        await store.RejectAsync(failed, attempt.LeaseToken!.Value, new("Failure", "SECRET-REJECTION", null));
        var flow = new FlowBuilder("history.incident", 1).Step("first", Invoice, "SECRET-TEMPLATE")
            .Step("second", Invoice, "input").After("first").Build();
        var run = await new WorkflowClient(store).StartAsync(flow, "input");
        var step = (await store.ClaimAsync(Claim()))!;
        await store.RejectAsync(step.Id, step.LeaseToken!.Value, new("Failure", "SECRET-INCIDENT", null));
        var active = await new JobClient(store).EnqueueAsync(Invoice, "input");
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload='SECRET-INVALID-JSON',output='SECRET-INVALID-JSON'");
        var policy = new ArchivePolicy(DateTimeOffset.UtcNow.AddDays(1), BatchSize: 1);
        var preview = await store.PreviewRetentionAsync(policy);
        Assert.Single(preview.Candidates);
        Assert.True(preview.HasMoreCandidates);
        Assert.False(preview.EvidenceSizeVerified);
        Assert.Equal("caller-supplied", preview.PolicySource);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(preview));
        var full = await store.PreviewRetentionAsync(policy with { BatchSize = 10 });
        Assert.Equal(new[] { first, second }.Order(), full.Candidates.Select(item => item.Id).Order());
        Assert.True(full.Candidates.Single(item => item.Id == first).MetadataTruncated);
        var includeFailure = policy with { BatchSize = 10, FailedBefore = policy.SucceededBefore };
        var candidates = await store.PreviewRetentionAsync(includeFailure);
        Assert.Contains(candidates.Candidates, item => item.Id == failed);
        Assert.DoesNotContain(candidates.Candidates, item => item.Id == run || item.Id == step.Id || item.Id == active);
        Assert.Equal((await store.ArchiveCandidatesAsync(includeFailure)).Select(item => item.Id), candidates.Candidates.Select(item => item.Id));
        Assert.Empty(await store.ArchivesAsync());
        Assert.Empty(await store.AuditAsync());
        Assert.NotNull((await store.FindExecutionAsync(first)).Execution);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task History_cursors_bind_kind_store_and_filters_and_validate_bounds(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await using var other = await StoreHarness.CreateAsync(provider);
        await harness.Store.SetQueuePausedAsync("one", true, 0, Command()).SucceedsAsync();
        await harness.Store.SetQueuePausedAsync("two", true, 0, Command()).SucceedsAsync();
        var query = new AuditQuery(Limit: 1);
        var cursor = (await harness.Store.SearchAuditAsync(query)).NextCursor!;
        Assert.NotNull(cursor);
        await Assert.ThrowsAsync<ArgumentException>(() => other.Store.SearchAuditAsync(query with { Cursor = cursor }));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchAuditAsync(query with { Cursor = cursor, Target = "one" }));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchArchivesAsync(new(Limit: 1, Cursor: cursor)));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchAuditAsync(new(Cursor: "invalid!")));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchAuditAsync(new(From: DateTimeOffset.UtcNow.AddDays(-8))));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.SearchArchivesAsync(new(Kind: (ArchiveKind)99)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Store.SearchAuditAsync(new(Limit: 101)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Store.PreviewRetentionAsync(new(DateTimeOffset.UtcNow, BatchSize: 11)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.SearchAuditAsync(new(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.SearchArchivesAsync(new(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Store.PreviewRetentionAsync(new(DateTimeOffset.UtcNow), cancelled.Token));
    }

    private static ClaimRequest Claim() => new("worker", [Invoice.Identity], ["default"], TimeSpan.FromMinutes(1));

    private static async Task<Guid> CompleteAsync(StoreHarness harness, string correlation)
    {
        var id = await new JobClient(harness.Store).EnqueueAsync(Invoice, "SECRET-INPUT", new(CorrelationId: correlation));
        var job = (await harness.Store.ClaimAsync(Claim()))!;
        await harness.Store.CompleteAsync(id, job.LeaseToken!.Value, JobResult.Done());

        return id;
    }

    private static Task ExecuteAsync(StoreHarness harness, string sql, params (string Name, object Value)[] values) =>
        harness.ExecuteAsync(sql, values);
}
