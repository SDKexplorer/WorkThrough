using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>Grants the tenants named by trusted "tenant" claims, or by a "tenant-" subject; everyone else is store-wide.</summary>
internal sealed class ClaimTenantResolver : ITenantAccessResolver
{
    public ValueTask<TenantAccess> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tenants = user.FindAll(TenantIsolationTests.TenantClaim).Select(claim => claim.Value).ToArray();
        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (tenants.Length == 0 && subject?.StartsWith(TenantIsolationTests.TenantUserPrefix, StringComparison.Ordinal) == true)
        {
            tenants = [subject[TenantIsolationTests.TenantUserPrefix.Length..]];
        }

        return ValueTask.FromResult(tenants.Length > 0 ? TenantAccess.Only(tenants) : TenantAccess.AllTenants);
    }
}

/// <summary>Tenant-scoped principals see and operate on their tenants only; store-wide surfaces are denied (ADR-019).</summary>
public sealed class TenantIsolationTests
{
    internal const string TenantClaim = "tenant";
    internal const string TenantUserPrefix = "tenant-user:";

    [Fact]
    public async Task Dashboard_scopes_reads_and_commands_to_the_principals_tenants()
    {
        await using var harness = await StoreHarness.CreateAsync("postgres");
        var client = new JobClient(harness.Store);
        var own = await client.EnqueueAsync(new Alpha("own"), new(TenantId: "tenant-a"));
        var foreign = await client.EnqueueAsync(new Alpha("foreign"), new(TenantId: "tenant-b"));
        var untenanted = await client.EnqueueAsync(new Alpha("untenanted"));
        var flow = new FlowBuilder("tests.tenant", 1).Step<Alpha>("only", new Alpha("step")).Build();
        var foreignRun = await new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(TenantId: "tenant-b"));
        await using var app = await DashboardTests.StartDashboardAsync(harness, services => services.AddSingleton<ITenantAccessResolver, ClaimTenantResolver>());
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-User", TenantUserPrefix + "tenant-a");

        using var access = JsonDocument.Parse(await http.GetStringAsync("/jobs/api/access"));
        Assert.Equal(["tenant-a"], access.RootElement.GetProperty("tenants").EnumerateArray().Select(item => item.GetString()));
        using var page = JsonDocument.Parse(await http.GetStringAsync("/jobs/api/executions"));
        Assert.Equal([own.ToString()], page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Contains(own.ToString(), await http.GetStringAsync("/jobs/api/jobs"));
        Assert.DoesNotContain(foreign.ToString(), await http.GetStringAsync("/jobs/api/jobs"));
        Assert.DoesNotContain(untenanted.ToString(), await http.GetStringAsync("/jobs/api/jobs?status=Queued"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/jobs/api/jobs/{own}")).StatusCode);
        foreach (var hidden in new[] { foreign, untenanted })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/jobs/api/jobs/{hidden}")).StatusCode);
            Assert.Contains("\"execution\":null", await http.GetStringAsync($"/jobs/api/executions/{hidden}"));
            Assert.Contains("\"executionExists\":false", await http.GetStringAsync($"/jobs/api/executions/{hidden}/attempts"));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/jobs/api/flows/{foreignRun}")).StatusCode);
        using var readiness = JsonDocument.Parse(await http.GetStringAsync($"/jobs/api/executions/{own}/readiness"));
        Assert.Equal(JsonValueKind.Null, readiness.RootElement.GetProperty("capacity").ValueKind);
        Assert.Empty(readiness.RootElement.GetProperty("workerExamples").EnumerateArray());

        foreach (var storeWide in new[] { "/jobs/api/stats", "/jobs/api/metrics", "/jobs/api/audit", "/jobs/api/workers", "/jobs/api/admission",
            "/jobs/api/controls", "/jobs/api/archives", "/jobs/api/retention", "/jobs/api/schedules", "/jobs/api/definitions" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(storeWide)).StatusCode);
        }

        await AddCsrfAsync(http);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync($"/jobs/api/jobs/{foreign}/cancel", new DashboardCommand(Guid.NewGuid(), "Not my tenant", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/jobs/api/jobs/{own}/cancel", new DashboardCommand(Guid.NewGuid(), "My tenant", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/jobs/api/limits", new LimitCommand(new(null, 1), new(Guid.NewGuid(), "Store-wide")))).StatusCode);
        Assert.Equal(JobStatus.Queued, (await harness.Store.GetAsync(foreign))!.Status);
        Assert.Equal(JobStatus.Cancelled, (await harness.Store.GetAsync(own))!.Status);

        // Store-wide principals are unaffected by a registered resolver.
        http.DefaultRequestHeaders.Remove("X-Test-User");
        http.DefaultRequestHeaders.Add("X-Test-User", "operator");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/jobs/api/stats")).StatusCode);
        Assert.Contains(foreign.ToString(), await http.GetStringAsync("/jobs/api/jobs"));
    }

    [Fact]
    public void Tenant_access_is_bounded_and_validated()
    {
        Assert.False(TenantAccess.AllTenants.IsRestricted);
        Assert.True(TenantAccess.AllTenants.Allows(null));
        var access = TenantAccess.Only(["a", "b"]);
        Assert.True(access.Allows("a"));
        Assert.False(access.Allows("c"));
        Assert.False(access.Allows(null));
        Assert.Equal(["b"], TenantScoping.AllowedTenants(access, null, ["b", "c"]));
        Assert.Empty(TenantScoping.AllowedTenants(access, "c", null));
        Assert.Throws<ArgumentException>(() => TenantAccess.Only(Enumerable.Range(0, TenantAccess.MaximumTenants + 1).Select(i => $"t{i}")));
        Assert.Throws<ArgumentException>(() => TenantAccess.Only([" padded"]));
    }

    private static async Task AddCsrfAsync(HttpClient http)
    {
        var csrf = await http.GetAsync("/jobs/api/csrf");
        using var token = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        http.DefaultRequestHeaders.Add("Cookie", string.Join("; ", csrf.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RootElement.GetProperty("token").GetString());
    }
}

public sealed partial class McpTests
{
    [Theory, MemberData(nameof(Providers))]
    public async Task Tenant_scoped_mcp_principals_see_only_their_tenants_and_no_store_wide_tools(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var jobs = new JobClient(harness.Store);
        var own = await jobs.EnqueueAsync(new Alpha("own"), new(TenantId: "tenant-a"));
        var foreign = await jobs.EnqueueAsync(new Alpha("foreign"), new(TenantId: "tenant-b"));
        var flow = new FlowBuilder("tests.tenant", 1).Step<Alpha>("only", new Alpha("step")).Build();
        var foreignRun = await new WorkflowClient(harness.Store).StartAsync(flow, new { }, new FlowStart(TenantId: "tenant-b"));
        await using var server = await McpTestHost.CreateAsync(harness, tenants: new ClaimTenantResolver());
        using var http = server.Client(subject: TenantIsolationTests.TenantUserPrefix + "tenant-a", payloads: true, audit: true, retention: true);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new(McpTestHost.Resource),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        }, http));

        var tools = (await client.ListToolsAsync()).Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(McpTenantToolNames.Order(StringComparer.Ordinal), tools);
        var page = await client.CallToolAsync("workthrough_search_executions", new Dictionary<string, object?> { ["query"] = new { } });
        Assert.Equal([own.ToString()], page.StructuredContent!.Value.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        var hidden = await client.CallToolAsync("workthrough_get_execution", new Dictionary<string, object?> { ["id"] = foreign });
        Assert.Equal(JsonValueKind.Null, hidden.StructuredContent!.Value.GetProperty("execution").ValueKind);
        var run = await client.CallToolAsync("workthrough_get_flow", new Dictionary<string, object?> { ["runId"] = foreignRun });
        Assert.Equal(JsonValueKind.Null, run.StructuredContent!.Value.GetProperty("flow").ValueKind);
        foreach (var storeWide in new[] { "workthrough_get_metrics", "workthrough_get_audit", "workthrough_search_archives", "workthrough_get_retention" })
        {
            Assert.True((await client.CallToolAsync(storeWide, new Dictionary<string, object?> { ["query"] = new { } })).IsError);
        }
    }

    private static readonly string[] McpTenantToolNames =
    [
        "workthrough_get_context", "workthrough_search_executions", "workthrough_get_execution", "workthrough_get_attempts",
        "workthrough_get_diagnostics", "workthrough_get_flow", "workthrough_get_capacity"
    ];
}
