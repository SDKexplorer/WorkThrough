using Xunit;

namespace WorkThrough.Tests;

public sealed partial class McpTests
{
    [Theory, MemberData(nameof(Providers))]
    public async Task History_tools_enforce_separate_permissions_and_return_metadata_on_read_only_connections(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var command = new AdministrativeCommand(Guid.NewGuid(), new("test-issuer", "operator", "SECRET-DISPLAY"), "SECRET-REASON");
        await harness.Store.SetQueuePausedAsync("separate-queue", true, 0, command).SucceedsAsync();
        var definition = new JobDefinition<string>("history.mcp");
        var id = await new JobClient(harness.Store).EnqueueAsync(definition, "SECRET-INPUT");
        var job = (await harness.Store.ClaimAsync(new("worker", [definition.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await harness.Store.CompleteAsync(id, job.LeaseToken!.Value, JobResult.Done());
        await using var server = await McpTestHost.CreateAsync(harness);
        using var viewer = server.Client();
        object auditRequest = new { name = "workthrough_get_audit", arguments = new { query = new { } } };
        object previewRequest = new { name = "workthrough_preview_retention", arguments = new { policy = new { succeededBefore = DateTimeOffset.UtcNow.AddDays(1) } } };
        foreach (var request in new[] { auditRequest, previewRequest })
        {
            var denied = await ResultAsync(await CallAsync(viewer, "tools/call", request));
            Assert.Equal("forbidden", denied.GetProperty("structuredContent").GetProperty("code").GetString());
        }

        using var auditOnly = server.Client(audit: true);
        var discovery = await ResultAsync(await CallAsync(auditOnly, "tools/list"));
        var names = discovery.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Contains("workthrough_get_audit", names);
        Assert.DoesNotContain("workthrough_preview_retention", names);
        var audit = await ResultAsync(await CallAsync(auditOnly, "tools/call", auditRequest));
        Assert.Equal(command.Id, audit.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("commandId").GetGuid());
        Assert.DoesNotContain("SECRET", audit.GetRawText());
        using var noStoreAccess = server.Client(permission: false, audit: true, retention: true);
        Assert.True((await ResultAsync(await CallAsync(noStoreAccess, "tools/call", auditRequest))).GetProperty("isError").GetBoolean());
        using var maintenanceViewer = server.Client(retention: true);
        var preview = await ResultAsync(await CallAsync(maintenanceViewer, "tools/call", previewRequest));
        Assert.Equal(id, preview.GetProperty("structuredContent").GetProperty("candidates")[0].GetProperty("id").GetGuid());
        Assert.Equal("caller-supplied", preview.GetProperty("structuredContent").GetProperty("policySource").GetString());
        Assert.DoesNotContain("SECRET", preview.GetRawText());
        Assert.Empty(await harness.Store.ArchivesAsync());
        Assert.Single(await harness.Store.AuditAsync());
        Assert.NotNull((await harness.Store.FindExecutionAsync(id)).Execution);
        Assert.Contains(server.Audit.Events, entry => entry.Tool == "workthrough_get_audit" && entry.SensitiveRead && entry.Outcome == "succeeded");
        using var all = server.Client(payloads: true, audit: true, retention: true);
        var fullDiscovery = await ResultAsync(await CallAsync(all, "tools/list"));
        Assert.Equal(13, fullDiscovery.GetProperty("tools").GetArrayLength());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Automatic_retention_status_is_read_only_permission_gated_and_excludes_lease_secrets(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await harness.Store.ConfigureRetentionAsync(new(TimeSpan.FromDays(7), Enabled: true), 0,
            new(Guid.NewGuid(), new("issuer", "operator"), "SECRET-REASON")).SucceedsAsync();
        var lease = (await harness.Store.AcquireMaintenanceAsync("SECRET-OWNER", new("issuer", "service"), TimeSpan.FromMinutes(1)))!;
        await using var server = await McpTestHost.CreateAsync(harness);
        using var viewer = server.Client();
        var request = new { name = "workthrough_get_retention", arguments = new { } };
        Assert.Equal("forbidden", (await ResultAsync(await CallAsync(viewer, "tools/call", request))).GetProperty("structuredContent").GetProperty("code").GetString());
        using var permitted = server.Client(retention: true);
        var result = await ResultAsync(await CallAsync(permitted, "tools/call", request));
        Assert.True(result.GetProperty("structuredContent").GetProperty("maintenanceActive").GetBoolean());
        Assert.DoesNotContain("SECRET", result.GetRawText());
        Assert.DoesNotContain(lease.Token.ToString(), result.GetRawText());
        Assert.Single(await harness.Store.AuditAsync());
        await using var missing = await McpTestHost.CreateAsync(harness, includeRetention: false);
        using var missingClient = missing.Client(retention: true);
        Assert.Equal("capability_unavailable", (await ResultAsync(await CallAsync(missingClient, "tools/call", request))).GetProperty("structuredContent").GetProperty("code").GetString());
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Archive_lookups_use_archive_resource_policy_and_do_not_allow_export_or_purge(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var definition = new JobDefinition<string>("history.archive");
        var id = await new JobClient(harness.Store).EnqueueAsync(definition, "SECRET-INPUT");
        var job = (await harness.Store.ClaimAsync(new("worker", [definition.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await harness.Store.CompleteAsync(id, job.LeaseToken!.Value, JobResult.Done());
        await harness.Store.ArchiveCompletedAsync(new(DateTimeOffset.UtcNow.AddDays(1)), new(Guid.NewGuid(), new("tests", "operator"), "SECRET-REASON")).SucceedsAsync();
        await using var blocked = await McpTestHost.CreateAsync(harness, deniedArchive: id);
        using var denied = blocked.Client();
        var request = new { name = "workthrough_get_archive", arguments = new { archiveId = id } };
        var error = await ResultAsync(await CallAsync(denied, "tools/call", request));
        Assert.Equal("forbidden", error.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Contains(blocked.Audit.Events, entry => entry.ArchiveId == id && entry.Outcome == "forbidden");
        await using var server = await McpTestHost.CreateAsync(harness);
        using var client = server.Client();
        var found = await ResultAsync(await CallAsync(client, "tools/call", request));
        Assert.Equal(id, found.GetProperty("structuredContent").GetProperty("archive").GetProperty("id").GetGuid());
        Assert.DoesNotContain("SECRET", found.GetRawText());
        var searched = await ResultAsync(await CallAsync(client, "tools/call", new { name = "workthrough_search_archives", arguments = new { query = new { kind = "Job" } } }));
        Assert.Equal(1, searched.GetProperty("structuredContent").GetProperty("items").GetArrayLength());
        foreach (var name in new[] { "workthrough_export_archive", "workthrough_purge_archive", "workthrough_archive_completed" })
        {
            Assert.True((await ResultAsync(await CallAsync(client, "tools/call", new { name, arguments = new { archiveId = id } }))).GetProperty("isError").GetBoolean());
        }

        Assert.True(Assert.Single(await harness.Store.ArchivesAsync()).BodyAvailable);
        Assert.Single(await harness.Store.AuditAsync());
    }

    [Fact]
    public async Task Missing_history_port_hides_tools_and_rejects_direct_calls_with_explicit_coverage()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        await using var server = await McpTestHost.CreateAsync(harness, includeHistory: false);
        using var client = server.Client(payloads: true, audit: true, retention: true);
        var listed = await ResultAsync(await CallAsync(client, "tools/list"));
        Assert.Equal(9, listed.GetProperty("tools").GetArrayLength());
        foreach (var name in new[] { "workthrough_get_audit", "workthrough_search_archives", "workthrough_get_archive", "workthrough_preview_retention" })
        {
            var result = await ResultAsync(await CallAsync(client, "tools/call", new { name, arguments = new { archiveId = Guid.NewGuid() } }));
            Assert.Equal("capability_unavailable", result.GetProperty("structuredContent").GetProperty("code").GetString());
        }

        var context = await ResultAsync(await CallAsync(client, "tools/call", new { name = "workthrough_get_context" }));
        Assert.Contains(context.GetProperty("structuredContent").GetProperty("unavailableCapabilities").EnumerateArray(), value => value.GetString() == "retention-preview");
    }
}
