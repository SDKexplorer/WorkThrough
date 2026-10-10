using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

public sealed class JobDashboardOptions
{
    /// <summary>Explicit opt-in for isolated development hosts. Production should use host authentication.</summary>
    public bool AllowAnonymous { get; set; }
    public string? AuthorizationPolicy { get; set; }
}

public static partial class DashboardEndpoints
{
    private const int JobPageSize = 100;
    private const string ConsoleResourcePrefix = "WorkThrough.Console.";
    private static readonly JsonSerializerOptions JsonOptions = new(JobJson.Options) { Converters = { new JsonStringEnumConverter() } };

    public static IServiceCollection AddWorkThroughDashboard(this IServiceCollection services)
    {
        services.AddAuthorization(authorization =>
        {
            foreach (var permission in OperationPermissions.All)
            {
                authorization.AddPolicy(OperationPermissions.Policy(permission), policy =>
                    policy.RequireAuthenticatedUser().RequireClaim(OperationPermissions.ClaimType, permission));
            }
        });
        services.AddAntiforgery(options => options.HeaderName = CsrfHeader);

        return services;
    }

    /// <summary>Hosts bundled UI and APIs at a chosen path, with no processor dependency.</summary>
    public static RouteGroupBuilder MapWorkThroughDashboard(this IEndpointRouteBuilder endpoints, string path = "/jobs", Action<JobDashboardOptions>? configure = null)
    {
        if (!Regex.IsMatch(path, "^/[a-zA-Z0-9/_-]*$"))
        {
            throw new ArgumentException("Dashboard path must be a literal URL path.", nameof(path));
        }

        var options = new JobDashboardOptions();
        configure?.Invoke(options);
        if (!options.AllowAnonymous && endpoints.ServiceProvider.GetService<IAuthenticationSchemeProvider>() is null)
        {
            throw new InvalidOperationException("Configure host authentication for the dashboard, or explicitly opt into AllowAnonymous for isolated development.");
        }

        var group = endpoints.MapGroup(path.TrimEnd('/'));
        if (options.AllowAnonymous)
        {
            group.AllowAnonymous();
        }
        else if (options.AuthorizationPolicy is { } policy)
        {
            group.RequireAuthorization(policy);
        }
        else
        {
            group.RequireAuthorization();
        }

        group.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            invocation.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";

            return await next(invocation);
        });
        MapConsole(group, options);
        MapExecutionReads(group, options);
        MapSchedules(group, options);
        MapAdministration(group, options);
        MapOperations(group, options);
        MapExecutionQueries(group, options);
        MapRetention(group, options);

        return group;
    }

    /// <summary>Bundled UI, its assets, anti-forgery tokens and the caller's effective permissions.</summary>
    private static void MapConsole(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/", (HttpContext context) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self' 'nonce-{nonce}'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
            var matchedPath = context.Request.PathBase + context.Request.Path.Value!.TrimEnd('/');
            using var index = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream(ConsoleResourcePrefix + "index.html");
            if (index is not null)
            {
                using var reader = new StreamReader(index);
                var html = reader.ReadToEnd().Replace("<base href=\"./\"", $"<base href=\"{HtmlEncoder.Default.Encode(matchedPath)}/\"")
                    .Replace("<body>", $"<body data-base=\"{HtmlEncoder.Default.Encode(matchedPath)}\">")
                    .Replace("<app-console", $"<app-console ngCspNonce=\"{nonce}\"");

                return Results.Content(html, "text/html");
            }

            return Unavailable("The dashboard bundle is missing. Build dashboard-ui before building the dashboard host.");
        });
        group.MapGet("/{asset}", (string asset) =>
        {
            if (!Regex.IsMatch(asset, "^[A-Za-z0-9_-]+\\.(js|css)$"))
            {
                return Results.NotFound();
            }

            using var resource = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream(ConsoleResourcePrefix + asset);
            if (resource is null)
            {
                return Results.NotFound();
            }

            using var reader = new StreamReader(resource);

            return Results.Content(reader.ReadToEnd(), asset.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/css");
        });
        group.MapGet("/api/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Json(new
            {
                token = antiforgery.GetAndStoreTokens(context).RequestToken
            }));
        group.MapGet("/api/access", async (HttpContext context) =>
        {
            var allowed = new List<string>();
            foreach (var permission in OperationPermissions.All)
            {
                if (await AllowedAsync(context, options, permission))
                {
                    allowed.Add(permission);
                }
            }

            return Results.Json(new
            {
                permissions = allowed,
                user = context.User.Identity?.Name ?? DevelopmentUser
            });
        });
    }

    /// <summary>Metadata reads. Payloads, outputs and errors additionally require payload access.</summary>
    private static void MapExecutionReads(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/jobs", async (string? correlationId, string? status, int? offset, IJobQueries queries, CancellationToken ct) =>
        {
            JobStatus? parsedStatus = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<JobStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Results.BadRequest(new { error = "Unknown job status." });
                }

                parsedStatus = parsed;
            }

            if (offset is < 0)
            {
                return Results.BadRequest(new { error = "Offset cannot be negative." });
            }

            var jobs = await queries.ListAsync(new(correlationId, parsedStatus, JobPageSize, offset ?? 0), ct);

            return Results.Json(jobs.Select(x => Metadata(x)), JsonOptions);
        }).WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/stats", async (IJobQueries queries, CancellationToken ct) => Results.Json(await queries.StatisticsAsync(ct), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/jobs/{id:guid}", async (Guid id, HttpContext context, IJobQueries queries, IJobDiagnostics diagnostics, CancellationToken ct) =>
        {
            if (await queries.GetAsync(id, ct) is not { } job)
            {
                return Results.NotFound();
            }

            var sensitive = await AllowedAsync(context, options, OperationPermissions.ReadPayloads);
            var health = await diagnostics.HealthAsync(id, ct);
            if (!sensitive && health?.Progress is { } progress)
            {
                health = health with { Progress = progress with { Description = null } };
            }

            return Results.Json(new
            {
                job = Metadata(job),
                health,
                sensitive,
                payload = sensitive ? (JsonElement?)job.Payload : null,
                output = sensitive ? job.Output : null,
                error = sensitive ? job.Error : null,
                attempts = (await queries.AttemptsAsync(id, ct)).Select(a => sensitive ? a : a with { Error = null }),
                failures = sensitive ? await diagnostics.FailuresAsync(id, ct) : null
            }, JsonOptions);
        }).WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/flows/{id:guid}", async (Guid id, IJobQueries queries, IWorkflowDefinitions definitions, IJobDiagnostics diagnostics, CancellationToken ct) =>
        {
            if (await queries.GetWorkflowAsync(id, ct) is not { } flow)
            {
                return Results.NotFound();
            }

            var definition = await definitions.GetRunDefinitionAsync(id, ct);

            return Results.Json(new
            {
                flow = flow with { Error = null },
                graph = definition is null ? null : Graph(definition),
                jobs = (await queries.ListFlowJobsAsync(id, ct)).Select(Metadata),
                events = await diagnostics.EventsAsync(id, cancellationToken: ct)
            }, JsonOptions);
        }).WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/definitions", async (IWorkflowDefinitions definitions, CancellationToken ct) =>
            Results.Json((await definitions.DefinitionsAsync(cancellationToken: ct)).Select(d => new { d.Definition.Key, d.Definition.Version, d.Status, d.Revision, graph = Graph(d.Definition) }), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/workers", async (IJobDiagnostics diagnostics, CancellationToken ct) => Results.Json(await diagnostics.WorkersAsync(ct), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/deployment", async (IDeploymentQueries queries, IJobDiagnostics diagnostics, CancellationToken ct) =>
        {
            var requirements = await queries.DeploymentRequirementsAsync(ct);
            var workers = (await diagnostics.WorkersAsync(ct)).Where(w => w.SeenAt > requirements.ObservedAt - WorkerHeartbeat.Freshness)
                .Select(w => new WorkerCapability(w.WorkerId, w.Processors, w.Queues)).ToArray();

            return Results.Json(DeploymentPreflight.Compare(requirements, workers), JsonOptions);
        }).WithPermission(options, OperationPermissions.ReadExecutions);
    }

    /// <summary>Schedule reads and lifecycle commands.</summary>
    private static void MapSchedules(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/schedules", async (IJobSchedules schedules, CancellationToken ct) =>
            Results.Json((await schedules.SchedulesAsync(ct)).Select(s => new
            {
                s.Definition.Key,
                s.Definition.Version,
                s.Definition.Cron,
                s.Definition.TimeZone,
                s.Definition.Processor,
                s.Definition.Overlap,
                s.Definition.Misfire,
                s.Status,
                s.Revision,
                s.Enabled,
                s.NextOccurrence
            }), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/schedules/{key}/occurrences", async (string key, IJobSchedules schedules, CancellationToken ct) => Results.Json(await schedules.OccurrencesAsync(key, ct), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapPost("/api/schedules/draft", async (ScheduleDraftCommand draft, HttpContext context, IJobSchedules schedules, CancellationToken ct) =>
            ToHttp(await schedules.SaveScheduleDraftAsync(draft.Definition, draft.Command.ExpectedRevision, Command(context, options, draft.Command), ct)))
            .Mutation(options, OperationPermissions.ManageSchedules);
        group.MapPost("/api/schedules/{key}/{version:int}/publish", async (string key, int version, DashboardCommand command, HttpContext context, IJobSchedules schedules, CancellationToken ct) =>
            ToHttp(await schedules.PublishScheduleAsync(key, version, command.ExpectedRevision, Command(context, options, command), ct)))
            .Mutation(options, OperationPermissions.ManageSchedules);
        foreach (var enable in new[] { true, false })
        {
            group.MapPost(enable ? "/api/schedules/{key}/{version:int}/enable" : "/api/schedules/{key}/{version:int}/pause", async (string key, int version, DashboardCommand command, HttpContext context, IJobSchedules schedules, CancellationToken ct) =>
                ToHttp(await schedules.SetScheduleEnabledAsync(key, version, command.ExpectedRevision, enable, Command(context, options, command), ct)))
                .Mutation(options, OperationPermissions.ManageSchedules);
        }
    }

    /// <summary>Audited job, flow, definition and limit commands, plus the audit trail.</summary>
    private static void MapAdministration(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/audit", async (IJobAdministration administration, CancellationToken ct) => Results.Json(await administration.AuditAsync(cancellationToken: ct), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadAudit);
        group.MapPost("/api/jobs/{id:guid}/retry", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.RetryJobAsync(id, command.ExpectedAttempt, Command(context, options, command), ct)))
            .Mutation(options, OperationPermissions.RetryJobs);
        group.MapPost("/api/jobs/{id:guid}/cancel", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.CancelJobAsync(id, command.ExpectedAttempt, Command(context, options, command), ct)))
            .Mutation(options, OperationPermissions.CancelJobs);
        group.MapPost("/api/jobs/{id:guid}/recover", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.RecoverStepAsync(id, command.ExpectedAttempt, Command(context, options, command), ct)))
            .Mutation(options, OperationPermissions.RecoverFlows);
        foreach (var pause in new[] { true, false })
        {
            group.MapPost(pause ? "/api/flows/{id:guid}/pause" : "/api/flows/{id:guid}/resume", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
                ToHttp(await administration.SetFlowPausedAsync(id, pause, Command(context, options, command), ct)))
                .Mutation(options, OperationPermissions.RecoverFlows);
        }

        group.MapPost("/api/definitions/draft", async (DraftCommand draft, HttpContext context, IWorkflowDefinitions definitions, CancellationToken ct) =>
            ToHttp(await definitions.SaveDraftAsync(draft.Definition, draft.Command.ExpectedRevision, Command(context, options, draft.Command), ct)))
            .Mutation(options, OperationPermissions.EditDefinitions);
        group.MapPost("/api/definitions/{key}/{version:int}/publish", async (string key, int version, DashboardCommand command, HttpContext context, IWorkflowDefinitions definitions, CancellationToken ct) =>
            ToHttp(await definitions.PublishAsync(key, version, command.ExpectedRevision, Command(context, options, command), ct)))
            .Mutation(options, OperationPermissions.PublishDefinitions);
        group.MapPost("/api/definitions/{key}/{version:int}/retire", async (string key, int version, DashboardCommand command, HttpContext context, IWorkflowDefinitions definitions, CancellationToken ct) =>
            ToHttp(await definitions.RetireAsync(key, version, command.ExpectedRevision, Command(context, options, command), ct)))
            .Mutation(options, OperationPermissions.RetireDefinitions);
        group.MapPost("/api/limits", async (LimitCommand request, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.ChangeLimitAsync(request.Limit, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageLimits);
        group.MapGet("/api/admission", ([FromServices] IAdmissionPolicies? policies, CancellationToken ct) =>
            ExecutionQueryResultAsync(policies, service => service.AdmissionPoliciesAsync(ct), ct))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapPost("/api/limits/partition", async (PartitionLimitCommand request, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.SetPartitionLimitAsync(request.Limit, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageLimits);
        group.MapPost("/api/limits/partition/remove", async (PolicyRemovalCommand request, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.RemovePartitionLimitAsync(request.JobKey, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageLimits);
        group.MapPost("/api/limits/rate", async (RateLimitCommand request, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.SetRateLimitAsync(request.Limit, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageLimits);
        group.MapPost("/api/limits/rate/remove", async (PolicyRemovalCommand request, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            ToHttp(await administration.RemoveRateLimitAsync(request.JobKey, request.PerPartition, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageLimits);
    }

    private static object Metadata(JobRecord job) => new
    {
        job.Id,
        job.Contract,
        job.Queue,
        job.CorrelationId,
        job.Status,
        job.Attempt,
        job.MaxAttempts,
        job.CreatedAt,
        job.AvailableAt,
        job.WorkflowRunId,
        job.StepId,
        job.WorkerId,
        job.LeaseExpiresAt,
        job.ExpiresAt,
        job.CausationJobId,
        job.PartitionKey
    };

    private static object Graph(FlowDefinition definition) => new
    {
        definition.Key,
        definition.Version,
        definition.FailurePolicy,
        steps = definition.Steps.Select(s => new { s.Id, s.DisplayName, s.Contract, s.After, s.Queue, signal = s.Signal, conditional = s.Condition is not null })
    };
}
