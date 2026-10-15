using System.Text;
using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class RetentionTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();
    public static IEnumerable<object[]> LegacyProviders() => Providers().Where(row => (string)row[0] is "postgres");
    private static readonly JobDefinition<string> Invoice = new("retention.invoice");
    private static readonly OperationActor Actor = new("tests", "maintenance");
    private static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "retention fixture");
    private static RetentionPolicy Policy(int batchSize = 10) => new(TimeSpan.FromMilliseconds(1), Enabled: true, BatchSize: batchSize);
    private static ClaimRequest Claim() => new("worker", [Invoice.Identity], ["default"], TimeSpan.FromMinutes(1));
    private static async Task<MaintenanceLease> Lease(RelationalJobStore store) =>
        (await store.AcquireMaintenanceAsync("test-host", Actor, TimeSpan.FromMinutes(1)))!;

    [Theory, MemberData(nameof(Providers))]
    public async Task Maintenance_is_opt_in_and_holds_block_manual_and_automatic_archive_in_current_transaction(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        Assert.Null(await store.AcquireMaintenanceAsync("host", Actor, TimeSpan.FromMinutes(1)));
        Assert.Null((await store.ReadRetentionStatusAsync()).Configuration);
        var id = await CompletedAsync(harness);
        var preview = await store.ArchiveCandidatesAsync(new(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Contains(preview, unit => unit.Id == id);
        var holdCommand = Command();
        var hold = await store.SetRetentionHoldAsync(id, true, 0, holdCommand).SucceedsAsync();
        Assert.Equal(hold, await harness.NewStore().SetRetentionHoldAsync(id, true, 0, holdCommand).SucceedsAsync());
        Assert.Equal(1, (await store.ReadRetentionStatusAsync()).HeldUnits);
        Assert.Empty(await store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), Command()).SucceedsAsync());
        var policyCommand = Command();
        Assert.Equal(1, await store.ConfigureRetentionAsync(Policy(), 0, policyCommand).SucceedsAsync());
        Assert.Equal(1, await store.ConfigureRetentionAsync(Policy(), 0, policyCommand).SucceedsAsync());
        await store.ConfigureRetentionAsync(Policy(), 0, Command()).FailsAsync(ErrorType.Conflict);
        Assert.Empty((await store.RunMaintenanceBatchAsync(await Lease(store))).Archived);
        Assert.NotNull(await store.GetAsync(id));
        await store.SetRetentionHoldAsync(id, false, 0, Command()).FailsAsync(ErrorType.Conflict);
        await store.SetRetentionHoldAsync(id, false, 1, Command()).SucceedsAsync();
        var result = await store.RunMaintenanceBatchAsync(await Lease(store));
        Assert.Equal(new[] { id }, result.Archived);
        Assert.Null(await store.GetAsync(id));
        Assert.True((await store.ArchivesAsync()).Single().BodyAvailable);
        await store.ConfigureRetentionAsync(Policy() with { PurgeAfter = TimeSpan.FromDays(7) }, 1, Command()).FailsAsync(ErrorType.Validation);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Competing_hosts_reacquire_the_same_batch_and_reconcile_lost_acknowledgements(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await CompletedAsync(harness);
        await store.ConfigureRetentionAsync(Policy(), 0, Command()).SucceedsAsync();
        var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() => harness.NewStore().AcquireMaintenanceAsync($"host:{index}", Actor, TimeSpan.FromMinutes(1)))));
        var first = Assert.Single(leases, value => value is not null)!;
        await store.RecordMaintenanceFailureAsync(first, budgetExceeded: true);
        Assert.Equal("BatchBudgetExceeded", (await store.ReadRetentionStatusAsync()).LastFailureCode);
        await ExecuteAsync(harness, "UPDATE dj_maintenance SET lease_expires_at=0");
        var replacement = await Lease(harness.NewStore());
        Assert.Equal(first.BatchId, replacement.BatchId);
        Assert.NotEqual(first.Token, replacement.Token);
        await Assert.ThrowsAsync<StateConflictException>(() => store.RunMaintenanceBatchAsync(first));
        await Assert.ThrowsAsync<StateConflictException>(() => store.ReadExportCandidatesAsync(first));
        await Assert.ThrowsAsync<StateConflictException>(() => store.RecordMaintenanceFailureAsync(first, budgetExceeded: false));
        Assert.NotNull(await store.GetAsync(id));
        var completed = await store.RunMaintenanceBatchAsync(replacement);
        // Drop the response, then reconcile through an independent store, including the old lease handle.
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(await harness.NewStore().RunMaintenanceBatchAsync(first)));
        Assert.Single(await store.AuditAsync(), entry => entry.Action == "retention.batch");
        var status = await store.ReadRetentionStatusAsync();
        Assert.Null(status.PendingBatchId);
        Assert.False(status.MaintenanceActive);
        Assert.Equal(completed.BatchId, status.LastBatch!.BatchId);
        Assert.Null(status.LastFailureCode);
        Assert.DoesNotContain(replacement.Token.ToString(), JsonSerializer.Serialize(status));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Policy_changes_supersede_pending_cutoffs_and_never_delete_using_stale_policy(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await CompletedAsync(harness);
        await harness.Store.ConfigureRetentionAsync(Policy(), 0, Command()).SucceedsAsync();
        var lease = await Lease(harness.Store);
        await harness.Store.ConfigureRetentionAsync(Policy() with { Enabled = false }, 1, Command()).SucceedsAsync();
        var result = await harness.Store.RunMaintenanceBatchAsync(lease);
        Assert.Equal("PolicySuperseded", result.Outcome);
        Assert.Empty(result.Archived);
        Assert.NotNull(await harness.Store.GetAsync(id));
        Assert.Null(await harness.Store.AcquireMaintenanceAsync("host", Actor, TimeSpan.FromMinutes(1)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Lease_loss_after_archive_writes_rolls_back_evidence_receipts_and_batch_result(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await CompletedAsync(harness, "rollback-receipt");
        await harness.Store.ConfigureRetentionAsync(Policy(), 0, Command()).SucceedsAsync();
        var lease = await Lease(harness.Store);
        await ExecuteAsync(harness, provider switch
        {
            "oracle" => "CREATE TRIGGER expire_batch AFTER INSERT ON dj_archives BEGIN UPDATE dj_maintenance SET lease_expires_at=0; END;",
            "sqlserver" => "CREATE TRIGGER expire_batch ON dj_archives AFTER INSERT AS UPDATE dj_maintenance SET lease_expires_at=0;",
            "mysql" or "mariadb" => "CREATE TRIGGER expire_batch AFTER INSERT ON dj_archives FOR EACH ROW UPDATE dj_maintenance SET lease_expires_at=0",
            _ => """
                CREATE FUNCTION expire_batch() RETURNS trigger AS $$ BEGIN
                  UPDATE dj_maintenance SET lease_expires_at=0; RETURN NEW; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER expire_batch AFTER INSERT ON dj_archives FOR EACH ROW EXECUTE FUNCTION expire_batch()
                """
        });
        await Assert.ThrowsAsync<StateConflictException>(() => harness.Store.RunMaintenanceBatchAsync(lease));
        Assert.NotNull(await harness.Store.GetAsync(id));
        Assert.Empty(await harness.Store.ArchivesAsync());
        Assert.DoesNotContain(await harness.Store.AuditAsync(), entry => entry.Action == "retention.batch");
        var pending = await harness.Store.ReadRetentionStatusAsync();
        Assert.Equal(lease.BatchId, pending.PendingBatchId);
        Assert.Null(pending.LastBatch);
        Assert.True(pending.MaintenanceActive);
        await ExecuteAsync(harness, provider != "postgres" ? "DROP TRIGGER expire_batch" : "DROP TRIGGER expire_batch ON dj_archives; DROP FUNCTION expire_batch()");
        Assert.Equal(new[] { id }, (await harness.Store.RunMaintenanceBatchAsync(lease)).Archived);
        Assert.Equal(id, await new JobClient(harness.Store).EnqueueAsync(Invoice, "input", new(IdempotencyKey: "rollback-receipt")));
    }

    [Theory, MemberData(nameof(LegacyProviders))]
    public async Task Schema_three_upgrades_preserve_invocations_and_create_disabled_maintenance_state(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var id = await new JobClient(harness.Store).EnqueueAsync(Invoice, "unchanged");
        await ExecuteAsync(harness, """
            DROP TABLE dj_retention_policy; DROP TABLE dj_retention_holds; DROP TABLE dj_retention_incidents;
            DROP TABLE dj_maintenance; DROP TABLE dj_maintenance_batches; DROP TABLE dj_archive_exports;
            DELETE FROM dj_schema; INSERT INTO dj_schema(version) VALUES (3)
            """);
        await harness.NewStore().InitializeAsync();
        await harness.NewStore().InitializeAsync();
        var job = (await harness.Store.GetAsync(id))!;
        Assert.Equal(Invoice.Identity, job.Contract);
        Assert.Equal("unchanged", job.Payload.GetString());
        Assert.Null((await harness.Store.ReadRetentionStatusAsync()).Configuration);
        Assert.Null(await harness.Store.AcquireMaintenanceAsync("host", Actor, TimeSpan.FromMinutes(1)));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Whole_flows_respect_step_holds_and_incident_dependencies_are_never_archived(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var flow = new FlowBuilder("retention.flow", 1).Step("first", Invoice, "one").Step("second", Invoice, "two").After("first").Build();
        var run = await new WorkflowClient(store).StartAsync(flow, "input");
        var first = (await store.ClaimAsync(Claim()))!;
        var hold = await store.SetRetentionHoldAsync(first.Id, true, 0, Command()).SucceedsAsync();
        Assert.Equal(run, hold.UnitId);
        await store.CompleteAsync(first.Id, first.LeaseToken!.Value, JobResult.Done());
        var second = (await store.ClaimAsync(Claim()))!;
        await store.CompleteAsync(second.Id, second.LeaseToken!.Value, JobResult.Done());
        var incidentRun = await new WorkflowClient(store).StartAsync(flow, "input");
        var failed = (await store.ClaimAsync(Claim()))!;
        await store.RejectAsync(failed.Id, failed.LeaseToken!.Value, new("Failure", "SECRET", null));
        await ExecuteAsync(harness, "UPDATE dj_jobs SET terminal_at=0 WHERE terminal_at IS NOT NULL");
        await store.ConfigureRetentionAsync(Policy() with { FailedAfter = TimeSpan.FromMilliseconds(1) }, 0, Command()).SucceedsAsync();
        Assert.Empty((await store.RunMaintenanceBatchAsync(await Lease(store))).Archived);
        await store.SetRetentionHoldAsync(run, false, 1, Command()).SucceedsAsync();
        Assert.Equal(new[] { run }, (await store.RunMaintenanceBatchAsync(await Lease(store))).Archived);
        Assert.Equal(FlowStatus.Incident, (await store.GetWorkflowAsync(incidentRun))!.Status);
        Assert.Equal(2, (await store.SearchExecutionsAsync(new(WorkflowRunId: incidentRun))).Items.Count);
        var package = JsonSerializer.Deserialize<ExecutionArchive>((await store.ExportArchiveAsync(run))!.Json, JobJson.Options)!;
        Assert.Equal(2, package.Jobs.Count);
        Assert.NotNull(package.Definition);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Purge_requires_current_batch_verified_copy_and_rechecks_holds_preserving_acceptance_receipts(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await CompletedAsync(harness, "once");
        var policy = Policy() with { PurgeAfter = TimeSpan.FromMilliseconds(1), ExportDestination = "test-vault-v1" };
        await store.ConfigureRetentionAsync(policy, 0, Command()).SucceedsAsync();
        await store.RunMaintenanceBatchAsync(await Lease(store));
        await ExecuteAsync(harness, "UPDATE dj_archives SET archived_at=0");
        var noProof = await store.RunMaintenanceBatchAsync(await Lease(store));
        Assert.Empty(noProof.Purged);
        var lease = await Lease(store);
        Assert.Single(await store.ReadExportCandidatesAsync(lease));
        var archive = (await store.ExportArchiveAsync(id))!;
        var receipt = new VerifiedArchiveExport(id, archive.Summary.Sha256, policy.ExportDestination!, "immutable-object", Encoding.UTF8.GetByteCount(archive.Json));
        await Assert.ThrowsAsync<StateConflictException>(() => store.RecordVerifiedExportAsync(lease, receipt with { Sha256 = "invalid" }));
        await Assert.ThrowsAsync<StateConflictException>(() => store.RecordVerifiedExportAsync(lease, receipt with { Destination = "another-vault" }));
        await store.RecordVerifiedExportAsync(lease, receipt);
        await store.SetRetentionHoldAsync(id, true, 0, Command()).SucceedsAsync();
        Assert.Empty((await store.RunMaintenanceBatchAsync(lease)).Purged);
        await store.PurgeArchiveAsync(id, receipt.Sha256, Command()).FailsAsync(ErrorType.Conflict);
        await store.SetRetentionHoldAsync(id, false, 1, Command()).SucceedsAsync();
        var next = await Lease(store);
        // A previous batch's proof is not reused automatically.
        Assert.Empty((await store.RunMaintenanceBatchAsync(next)).Purged);
        next = await Lease(store);
        await store.RecordVerifiedExportAsync(next, receipt);
        await ExecuteAsync(harness, "UPDATE dj_maintenance SET lease_expires_at=0");
        var recovered = await Lease(store);
        await Assert.ThrowsAsync<StateConflictException>(() => store.RecordVerifiedExportAsync(next, receipt));
        Assert.Equal(new[] { id }, (await store.RunMaintenanceBatchAsync(recovered)).Purged);
        Assert.False((await store.ArchivesAsync()).Single().BodyAvailable);
        Assert.Equal(id, await new JobClient(store).EnqueueAsync(Invoice, "input", new(IdempotencyKey: "once")));
        Assert.Empty(await store.ListAsync(new()));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Oversized_evidence_is_retained_and_does_not_starve_later_candidates(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var oversized = await CompletedAsync(harness);
        var normal = await CompletedAsync(harness);
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload=@payload,terminal_at=-1 WHERE id=@id", ("payload", JsonSerializer.Serialize(new string('x', 8_000_001))), ("id", oversized.ToString()));
        await harness.Store.ConfigureRetentionAsync(Policy(batchSize: 1), 0, Command()).SucceedsAsync();
        var first = await harness.Store.RunMaintenanceBatchAsync(await Lease(harness.Store));
        Assert.Equal(new[] { oversized }, first.Blocked);
        Assert.Equal(new[] { normal }, (await harness.Store.RunMaintenanceBatchAsync(await Lease(harness.Store))).Archived);
        var status = await harness.Store.ReadRetentionStatusAsync();
        Assert.Equal(1, status.IncidentCount);
        Assert.Equal("ArchiveEvidenceLimit", Assert.Single(status.IncidentExamples).Code);
        Assert.NotNull(await harness.Store.GetAsync(oversized));
        await ExecuteAsync(harness, "UPDATE dj_jobs SET payload='\"repaired\"' WHERE id=@id", ("id", oversized.ToString()));
        await harness.Store.RetryRetentionIncidentAsync(oversized, Command()).SucceedsAsync();
        Assert.Equal(new[] { oversized }, (await harness.Store.RunMaintenanceBatchAsync(await Lease(harness.Store))).Archived);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Journal_cleanup_respects_subscribers_live_evidence_and_holds(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var id = await CompletedAsync(harness);
        await store.SubscribeAsync("required-audit", 0);
        await store.ConfigureRetentionAsync(Policy() with { EventsAfter = TimeSpan.FromMilliseconds(1) }, 0, Command()).SucceedsAsync();
        await ExecuteAsync(harness, "UPDATE dj_events SET at=0");
        Assert.Equal(0, (await store.RunMaintenanceBatchAsync(await Lease(store))).EventsTrimmed);
        var end = (await store.EventPositionAsync()).LastSequence;
        await store.AcknowledgeAsync("required-audit", 0, end);
        await store.SetRetentionHoldAsync(id, true, 0, Command()).SucceedsAsync();
        await store.TrimEventsAsync(end, Command()).FailsAsync(ErrorType.Conflict);
        Assert.Equal(0, (await store.RunMaintenanceBatchAsync(await Lease(store))).EventsTrimmed);
        await store.SetRetentionHoldAsync(id, false, 1, Command()).SucceedsAsync();
        Assert.Equal(2, (await store.RunMaintenanceBatchAsync(await Lease(store))).EventsTrimmed);
        Assert.Equal(end, (await store.EventPositionAsync()).RetainedAfter);
        var active = await new JobClient(store).EnqueueAsync(Invoice, "active");
        await ExecuteAsync(harness, "UPDATE dj_events SET at=0");
        Assert.Equal(0, (await store.RunMaintenanceBatchAsync(await Lease(store))).EventsTrimmed);
        Assert.NotNull(await store.GetAsync(active));
        Assert.Equal(end, (await store.ReadRetentionStatusAsync()).SlowestSubscriberPosition);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Export_incidents_are_visible_and_cannot_starve_following_archives(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var store = harness.Store;
        var first = await CompletedAsync(harness);
        var second = await CompletedAsync(harness);
        await store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), Command()).SucceedsAsync();
        await ExecuteAsync(harness, "UPDATE dj_archives SET archived_at=0");
        await store.ConfigureRetentionAsync(Policy(1) with { PurgeAfter = TimeSpan.FromMilliseconds(1), ExportDestination = "vault" }, 0, Command()).SucceedsAsync();
        var lease = await Lease(store);
        var selected = Assert.Single(await store.ReadExportCandidatesAsync(lease));
        await store.RecordExportIncidentAsync(lease, selected.Id);
        Assert.NotEqual(selected.Id, Assert.Single(await store.ReadExportCandidatesAsync(lease)).Id);
        Assert.Empty((await store.RunMaintenanceBatchAsync(lease)).Purged);
        Assert.Equal("ArchiveExportVerificationFailed", Assert.Single((await store.ReadRetentionStatusAsync()).IncidentExamples).Code);
        Assert.NotNull(await store.ExportArchiveAsync(first));
        Assert.NotNull(await store.ExportArchiveAsync(second));
    }

    [Fact]
    public async Task File_export_is_immutable_verified_and_reusable_after_a_lost_receipt()
    {
        await using var harness = await StoreHarness.CreateAsync("postgres");
        var id = await CompletedAsync(harness);
        await harness.Store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), Command()).SucceedsAsync();
        var archive = (await harness.Store.ExportArchiveAsync(id))!;
        var storeId = (await harness.Store.GetExecutionContextAsync()).StoreId;
        var directory = Path.Combine(Path.GetTempPath(), "workthrough-export-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var destination = new FileArchiveDestination("vault-v1", directory);
            var first = await destination.StoreAndVerifyAsync(storeId, archive);
            Assert.Equal(first, await new FileArchiveDestination("vault-v1", directory).StoreAndVerifyAsync(storeId, archive));
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            Assert.Single(files);
            await File.WriteAllTextAsync(files[0], "corrupted");
            await Assert.ThrowsAsync<InvalidDataException>(() => destination.StoreAndVerifyAsync(storeId, archive));
            Assert.Equal("corrupted", await File.ReadAllTextAsync(files[0]));
            Assert.True((await harness.Store.ArchivesAsync()).Single().BodyAvailable);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Optional_background_service_runs_configured_policy_and_stops_cleanly()
    {
        await using var harness = await StoreHarness.CreateAsync("postgres");
        var id = await CompletedAsync(harness);
        await harness.Store.ConfigureRetentionAsync(Policy(), 0, Command()).SucceedsAsync();
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddWorkThrough().UsePostgreSql(harness.ConnectionString)
                .InitializeStorageOnStart(false).AddRetentionMaintenance(options => options.PollInterval = TimeSpan.FromSeconds(1))).Build();
        await host.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await harness.Store.GetAsync(id, timeout.Token) is not null)
            {
                await Task.Delay(25, timeout.Token);
            }

            Assert.Single(await harness.Store.ArchivesAsync());
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False((await harness.Store.ReadRetentionStatusAsync()).MaintenanceActive);
    }

    private static async Task<Guid> CompletedAsync(StoreHarness harness, string? idempotencyKey = null)
    {
        var id = await new JobClient(harness.Store).EnqueueAsync(Invoice, "input", new(IdempotencyKey: idempotencyKey));
        var claim = (await harness.Store.ClaimAsync(Claim()))!;
        Assert.Equal(id, claim.Id);
        Assert.True(await harness.Store.CompleteAsync(id, claim.LeaseToken!.Value, JobResult.Done()));
        await ExecuteAsync(harness, "UPDATE dj_jobs SET terminal_at=0 WHERE id=@id", ("id", id.ToString()));

        return id;
    }

    private static Task ExecuteAsync(StoreHarness harness, string sql, params (string Name, object Value)[] values) =>
        harness.ExecuteAsync(sql, values);
}
