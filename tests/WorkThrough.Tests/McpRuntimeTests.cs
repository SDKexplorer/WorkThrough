using System.Text.Json;
using Xunit;

namespace WorkThrough.Tests;

public sealed partial class McpTests
{
    [Theory, MemberData(nameof(Providers))]
    public async Task Runtime_tools_authorize_the_execution_and_use_read_only_metadata_queries(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var definition = new JobDefinition<string>("runtime.protected");
        var id = await new JobClient(harness.Store).EnqueueAsync(definition, "SECRET-PAYLOAD");
        await using var deniedServer = await McpTestHost.CreateAsync(harness, deniedId: id);
        using var deniedClient = deniedServer.Client();
        var request = new { name = "workthrough_get_capacity", arguments = new { id } };
        var denied = await ResultAsync(await CallAsync(deniedClient, "tools/call", request));
        Assert.Equal("forbidden", denied.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Contains(deniedServer.Audit.Events, entry => entry.ExecutionId == id && entry.Outcome == "forbidden");

        await using var server = await McpTestHost.CreateAsync(harness);
        using var client = server.Client();
        var readiness = await ResultAsync(await CallAsync(client, "tools/call", request));
        Assert.Equal("NoRecentCapableWorker", readiness.GetProperty("structuredContent").GetProperty("reason").GetString());
        Assert.DoesNotContain("SECRET", readiness.GetRawText());
        var metrics = await ResultAsync(await CallAsync(client, "tools/call", new { name = "workthrough_get_metrics", arguments = new { query = new { } } }));
        Assert.Equal("live-attempts", metrics.GetProperty("structuredContent").GetProperty("coverage").GetString());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("structuredContent").GetProperty("meanDurationMilliseconds").ValueKind);
        var invalid = await ResultAsync(await CallAsync(client, "tools/call", new { name = "workthrough_get_capacity", arguments = new { id, workerExamples = 101 } }));
        Assert.True(invalid.GetProperty("isError").GetBoolean());
        Assert.Empty(await harness.Store.AuditAsync());
        Assert.Equal(0, (await harness.Store.GetAsync(id))!.Attempt);

        await using var unavailable = await McpTestHost.CreateAsync(harness, includeRuntime: false);
        using var missingClient = unavailable.Client();
        var missing = await ResultAsync(await CallAsync(missingClient, "tools/call", request));
        Assert.Equal("capability_unavailable", missing.GetProperty("structuredContent").GetProperty("code").GetString());
    }
}
