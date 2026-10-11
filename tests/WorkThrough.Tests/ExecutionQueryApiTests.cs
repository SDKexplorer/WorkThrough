using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ExecutionQueryApiTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    [Theory, MemberData(nameof(Providers))]
    public async Task Bounded_routes_enforce_permissions_and_return_metadata_only(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var processor = new JobDefinition<string>("queries.http", displayName: "HTTP query fixture");
        var id = await new JobClient(harness.Store).EnqueueAsync(processor, "SECRET-INPUT", new(CorrelationId: "invoice:123"));
        await new JobClient(harness.Store).EnqueueAsync(processor, "SECRET-SECOND", new(CorrelationId: "invoice:123"));
        var job = (await harness.Store.ClaimAsync(new("worker", [processor.Identity], ["default"], TimeSpan.FromMinutes(1))))!;
        await harness.Store.FailWithDetailsAsync(job.Id, job.LeaseToken!.Value, new("SECRET-TYPE", "SECRET-ERROR", "SECRET-STACK"), TimeSpan.Zero);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var work = builder.Services.AddWorkThrough();
        harness.Register(work);

        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, DashboardTests.TestAuthentication>("Test", _ =>
        {
        });
        builder.Services.AddWorkThroughDashboard();
        await using var app = builder.Build();
        app.MapWorkThroughDashboard("/operations");
        await app.StartAsync();
        var client = app.GetTestClient();
        const string endpoint = "/operations/api/executions";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(endpoint)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-User", "outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{endpoint}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{endpoint}/{id}/attempts")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "viewer");
        var response = await client.GetAsync($"{endpoint}?correlationId=invoice%3A123&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", json);
        using var first = JsonDocument.Parse(json);
        Assert.Equal("live", first.RootElement.GetProperty("coverage").GetString());
        Assert.Single(first.RootElement.GetProperty("items").EnumerateArray());
        var cursor = Uri.EscapeDataString(first.RootElement.GetProperty("nextCursor").GetString()!);
        var next = await client.GetStringAsync($"{endpoint}?correlationId=invoice%3A123&limit=1&cursor={cursor}");
        using var second = JsonDocument.Parse(next);
        Assert.NotEqual(first.RootElement.GetProperty("items")[0].GetProperty("id").GetString(), second.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.DoesNotContain("SECRET", await client.GetStringAsync($"{endpoint}/{job.Id}"));
        Assert.DoesNotContain("SECRET", await client.GetStringAsync($"{endpoint}/{job.Id}/attempts"));
        var failed = await client.GetStringAsync($"{endpoint}?timeBasis=FailedAttempt&processorKey={processor.Identity.Key}");
        Assert.Contains(job.Id.ToString(), failed);
        foreach (var invalid in new[] { "limit=101", "limit=0", "status=999", "timeBasis=999", "cursor=invalid", "contractVersion=2", "from=invalid-date" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{endpoint}?{invalid}")).StatusCode);
        }

        Assert.Empty(await harness.Store.AuditAsync());
    }
}
