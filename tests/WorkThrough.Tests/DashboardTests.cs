using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace WorkThrough.Tests;

public sealed class DashboardTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("/admin", "/mounted")]
    public async Task Dashboard_requires_authentication_and_reads_unknown_contracts_without_handlers(string groupPrefix, string pathBase)
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("<script>alert(1)</script>"), new(CorrelationId: "order:123"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IJobStore>(harness.Store);
        builder.Services.AddSingleton<IJobQueries>(harness.Store);
        builder.Services.AddSingleton<IJobDiagnostics>(harness.Store);
        builder.Services.AddSingleton<IJobAdministration>(harness.Store);
        builder.Services.AddSingleton<IWorkflowDefinitions>(harness.Store);
        builder.Services.AddSingleton<IJobSchedules>(harness.Store);
        builder.Services.AddSingleton<IDeploymentQueries>(harness.Store);
        builder.Services.AddSingleton<IExecutionOperations>(harness.Store);
        builder.Services.AddSingleton<IExecutionArchives>(harness.Store);
        builder.Services.AddSingleton<IProcessorRegistry>(harness.Store);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ =>
        {
        });
        builder.Services.AddWorkThroughDashboard();
        await using var app = builder.Build();
        app.UsePathBase(pathBase);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup(groupPrefix).MapWorkThroughDashboard("/internal/work");
        await app.StartAsync();
        var client = app.GetTestClient();
        var path = pathBase + groupPrefix + "/internal/work";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{path}/api/jobs")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-User", "operator");
        var response = await client.GetAsync($"{path}/api/jobs?correlationId=order%3A123");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), json);
        Assert.DoesNotContain("script", json); // List responses do not disclose payloads.
        var deployment = await client.GetStringAsync($"{path}/api/deployment");
        Assert.Contains("MissingProcessor", deployment);
        Assert.DoesNotContain("script", deployment);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/api/jobs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"{path}/api/executions")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"{path}/api/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"{path}/api/executions/{id}/readiness")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"{path}/api/jobs/{id}/cancel", null)).StatusCode);
        var html = await client.GetStringAsync(path);
        Assert.Contains($"<base href=\"{path}/\"", html);
        Assert.DoesNotContain("<script>alert", html);
        var stylesheet = System.Text.RegularExpressions.Regex.Match(html, "href=\"(styles-[A-Za-z0-9_-]+\\.css)\"");
        Assert.True(stylesheet.Success, "The embedded Angular stylesheet must be present.");
        var style = await client.GetAsync($"{path}/{stylesheet.Groups[1].Value}");
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);
        var csrf = await client.GetAsync($"{path}/api/csrf");
        using var tokenDocument = System.Text.Json.JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", csrf.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", tokenDocument.RootElement.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{path}/api/jobs/{id}/cancel", new DashboardCommand(Guid.NewGuid(), "Cancel obsolete request"))).StatusCode);
        Assert.Equal(JobStatus.Cancelled, (await harness.Store.GetAsync(id))!.Status);
        Assert.Equal("operator", (await harness.Store.AuditAsync()).Single().Actor.Subject);
    }

    [Fact]
    public void Production_dashboard_fails_fast_without_host_authentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWorkThroughDashboard();
        using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapWorkThroughDashboard());
    }

    [Fact]
    public async Task The_console_reports_the_engine_protocol_and_what_blocks_the_next_level()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        await RollingUpgradeTests.ExecuteAsync(harness, $"UPDATE dj_compatibility SET protocol={EngineProtocol.Baseline}");
        await harness.Store.HeartbeatAsync(new("older-worker", default, [], ["default"], 1, 0));
        await using var app = await StartDashboardAsync(harness);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "viewer");

        using var state = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/jobs/api/protocol"));
        Assert.Equal(EngineProtocol.Baseline, state.RootElement.GetProperty("active").GetInt32());
        Assert.Equal(EngineProtocol.Supported, state.RootElement.GetProperty("supported").GetInt32());
        Assert.Equal("older-worker", Assert.Single(state.RootElement.GetProperty("blockers").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Correcting_an_input_requires_the_payload_permission_too()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("bad"), new(MaxAttempts: 1));
        var job = (await harness.Store.ClaimAsync(new("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromSeconds(30))))!;
        Assert.True(await harness.Store.RejectAsync(id, job.LeaseToken!.Value, new("Rejected", "bad", null)));
        await using var app = await StartDashboardAsync(harness);
        var correction = new InputCorrectionCommand(System.Text.Json.JsonSerializer.SerializeToElement(new Alpha("good"), JobJson.Options),
            new(Guid.NewGuid(), "Fix the input", 1));

        foreach (var (user, expected) in new[] { ("corrector", HttpStatusCode.Forbidden), ("operator", HttpStatusCode.OK) })
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-User", user);
            var csrf = await client.GetAsync("/jobs/api/csrf");
            using var token = System.Text.Json.JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", csrf.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RootElement.GetProperty("token").GetString());
            Assert.Equal(expected, (await client.PostAsJsonAsync($"/jobs/api/jobs/{id}/correct", correction)).StatusCode);
        }

        Assert.Equal("good", (await harness.Store.GetAsync(id))!.Payload.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Metadata_viewer_cannot_read_exceptions_or_mutate_jobs_even_with_valid_csrf()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        var id = await new JobClient(harness.Store).EnqueueAsync(new Alpha("SECRET-PAYLOAD"), new(MaxAttempts: 1));
        var job = (await harness.Store.ClaimAsync(new("worker", [JobContractId.For<Alpha>()], ["default"], TimeSpan.FromSeconds(30))))!;
        await harness.Store.FailWithDetailsAsync(id, job.LeaseToken!.Value, new("Failure", "SECRET-MESSAGE", "SECRET-STACK"), TimeSpan.Zero);
        await using var app = await StartDashboardAsync(harness);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "viewer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/jobs/api/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/jobs/api/metrics?from=2025-01-01T00:00:00Z&until=2025-02-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/jobs/api/executions/{id}/readiness")).StatusCode);
        var detail = await client.GetStringAsync($"/jobs/api/jobs/{id}");
        Assert.DoesNotContain("SECRET", detail);
        Assert.Contains("\"sensitive\":false", detail);
        var csrf = await client.GetAsync("/jobs/api/csrf");
        using var token = System.Text.Json.JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", csrf.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RootElement.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/jobs/api/jobs/{id}/retry", new DashboardCommand(Guid.NewGuid(), "unauthorized", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/jobs/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/jobs/api/retention")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/jobs/api/retention/policy",
            new RetentionPolicyCommand(new(TimeSpan.FromDays(7), Enabled: true), new(Guid.NewGuid(), "unauthorized")))).StatusCode);
        Assert.Equal(JobStatus.Failed, (await harness.Store.GetAsync(id))!.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/jobs/api/jobs/{id}/replay", new DashboardCommand(Guid.NewGuid(), "unauthorized"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/jobs/api/queues", new QueueControlCommand("default", true, new(Guid.NewGuid(), "unauthorized")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/jobs/api/calendars",
            new CalendarCommand("holidays", new ScheduleCalendar(), new(Guid.NewGuid(), "unauthorized")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/jobs/api/archives/{id}/export")).StatusCode);
        Assert.Empty(await harness.Store.AuditAsync());
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "operator");
        Assert.Contains("SECRET-STACK", await client.GetStringAsync($"/jobs/api/jobs/{id}"));
        var retentionCommand = new RetentionPolicyCommand(new(TimeSpan.FromDays(7)), new(Guid.NewGuid(), "Configure disabled retention"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/jobs/api/retention/policy", retentionCommand)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/jobs/api/retention/policy", retentionCommand with { Command = new(Guid.NewGuid(), "Stale revision") })).StatusCode);
        Assert.Contains("\"enabled\":false", await client.GetStringAsync("/jobs/api/retention"));
        var holidays = new CalendarCommand("holidays", new ScheduleCalendar().ExcludingEveryYear(12, 25), new(Guid.NewGuid(), "Company holidays"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/jobs/api/calendars", holidays)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/jobs/api/calendars", holidays with { Command = new(Guid.NewGuid(), "Stale revision") })).StatusCode);
        Assert.Contains("\"name\":\"holidays\"", await client.GetStringAsync("/jobs/api/calendars"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/jobs/api/calendars/holidays/delete", new DashboardCommand(Guid.NewGuid(), "No longer used", ExpectedRevision: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/jobs/api/jobs/{Guid.NewGuid()}/retry",
            new DashboardCommand(Guid.NewGuid(), "Retry an execution that was never accepted"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs/api/limits",
            new LimitCommand(new(null, -1), new(Guid.NewGuid(), "Negative limit")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/jobs/api/limits/keyed",
            new KeyedLimitCommand(new(KeyScope.Partition, null, 1), new(Guid.NewGuid(), "One attempt per customer")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs/api/limits/rate",
            new RateLimitCommand(new(null, 5, TimeSpan.Zero), new(Guid.NewGuid(), "Window too short")))).StatusCode);
        var admission = await client.GetStringAsync("/jobs/api/admission");
        Assert.Contains("\"scope\":\"Partition\"", admission);
        Assert.Contains("\"maximumPerKey\":1", admission);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "unidentified");

        // Anti-forgery tokens are bound to the signed-in identity.
        using var unidentifiedToken = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/jobs/api/csrf"));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", unidentifiedToken.RootElement.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/jobs/api/jobs/{id}/retry", new DashboardCommand(Guid.NewGuid(), "No subject", 1))).StatusCode);
        var audit = await harness.Store.AuditAsync();
        Assert.Equal([AuditActions.DeleteCalendar, AuditActions.SaveCalendar, AuditActions.SetKeyedLimit, AuditActions.ConfigureRetention],
            audit.Select(entry => entry.Action).Order(StringComparer.Ordinal));
        Assert.All(audit, entry => Assert.Equal("operator", entry.Actor.Subject));
    }

    /// <summary>A dashboard over every port of one store, authenticated by <see cref="TestAuthentication"/>.</summary>
    internal static async Task<WebApplication> StartDashboardAsync(StoreHarness harness, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var store = harness.Store;
        builder.Services.AddSingleton<IJobStore>(store).AddSingleton<IJobQueries>(store).AddSingleton<IJobDiagnostics>(store)
            .AddSingleton<IJobAdministration>(store).AddSingleton<IWorkflowDefinitions>(store).AddSingleton<IJobSchedules>(store)
            .AddSingleton<IDeploymentQueries>(store).AddSingleton<IExecutionOperations>(store).AddSingleton<IExecutionArchives>(store)
            .AddSingleton<IProcessorRegistry>(store).AddSingleton<IAdmissionPolicies>(store).AddSingleton<IRuntimeQueries>(store)
            .AddSingleton<IRetentionQueries>(store).AddSingleton<IRetentionAdministration>(store).AddSingleton<IExecutionQueries>(store);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ =>
        {
        });
        builder.Services.AddWorkThroughDashboard();
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapWorkThroughDashboard();
        await app.StartAsync();

        return app;
    }

    public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {

            if (!Request.Headers.ContainsKey("X-Test-User"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var user = Request.Headers["X-Test-User"].ToString();
            var claims = new List<Claim> { new(ClaimTypes.Name, "operator") };
            if (user != "unidentified")
            {
                claims.Add(new(ClaimTypes.NameIdentifier, "operator", ClaimValueTypes.String, "test-issuer"));
            }

            IReadOnlyList<string> permissions = user switch
            {
                "viewer" => ["executions.read"],
                "corrector" => [OperationPermissions.ReadExecutions, OperationPermissions.CorrectInputs],
                "outsider" => [],
                _ => DashboardPermissions.All
            };
            claims.AddRange(permissions.Select(p => new Claim(DashboardPermissions.ClaimType, p)));
            if (user.StartsWith(TenantIsolationTests.TenantUserPrefix, StringComparison.Ordinal))
            {
                claims.Add(new(TenantIsolationTests.TenantClaim, user[TenantIsolationTests.TenantUserPrefix.Length..]));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
