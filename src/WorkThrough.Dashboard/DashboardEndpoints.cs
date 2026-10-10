using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JobJson.Options) { Converters = { new JsonStringEnumConverter() } };

    public static IServiceCollection AddWorkThroughDashboard(this IServiceCollection services)
    {
        services.AddAuthorization(authorization =>
        {
            foreach (var permission in DashboardPermissions.All)
            {
                authorization.AddPolicy(DashboardPermissions.PolicyPrefix + permission, policy =>
                    policy.RequireAuthenticatedUser().RequireClaim(DashboardPermissions.ClaimType, permission));
            }
        });
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

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
        group.MapGet("/", (HttpContext context) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self' 'nonce-{nonce}'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
            var matchedPath = context.Request.PathBase + context.Request.Path.Value!.TrimEnd('/');
            using var index = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream("WorkThrough.Console.index.html");
            if (index is not null)
            {
                using var reader = new StreamReader(index);
                var html = reader.ReadToEnd().Replace("<base href=\"./\"", $"<base href=\"{HtmlEncoder.Default.Encode(matchedPath)}/\"")
                    .Replace("<body>", $"<body data-base=\"{HtmlEncoder.Default.Encode(matchedPath)}\">")
                    .Replace("<app-console", $"<app-console ngCspNonce=\"{nonce}\"");

                return Results.Content(html, "text/html");
            }

            return Results.Problem("The dashboard bundle is missing. Build dashboard-ui before building the dashboard host.", statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        group.MapGet("/{asset}", (string asset) =>
        {
            if (!Regex.IsMatch(asset, "^[A-Za-z0-9_-]+\\.(js|css)$"))
            {
                return Results.NotFound();
            }

            using var resource = typeof(DashboardEndpoints).Assembly.GetManifestResourceStream("WorkThrough.Console." + asset);
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
            foreach (var permission in DashboardPermissions.All)
            {
                if (await AllowedAsync(context, options, permission))
                {
                    allowed.Add(permission);
                }
            }

            return Results.Json(new
            {
                permissions = allowed,
                user = context.User.Identity?.Name ?? "Development user"
            });
        });
        group.MapGet("/api/jobs", async (string? correlationId, string? status, int? offset, IJobQueries queries, CancellationToken ct) =>
        {
            JobStatus? parsedStatus = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<JobStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Results.BadRequest(new
                    {
                        error = "Unknown job status."
                    });
                }

                parsedStatus = parsed;
            }

            if (offset is < 0)
            {
                return Results.BadRequest(new
                {
                    error = "Offset cannot be negative."
                });
            }

            var jobs = await queries.ListAsync(new(correlationId, parsedStatus, 100, offset ?? 0), ct);

            return Results.Json(jobs.Select(x => Metadata(x)), JsonOptions);
        }).WithPermission(options, "executions.read");
        group.MapGet("/api/stats", async (IJobQueries queries, CancellationToken ct) => Results.Json(await queries.StatisticsAsync(ct), JsonOptions))
            .WithPermission(options, "executions.read");
        group.MapGet("/api/jobs/{id:guid}", async (Guid id, HttpContext context, IJobQueries queries, IJobDiagnostics diagnostics, CancellationToken ct) =>
        {
            if (await queries.GetAsync(id, ct) is not { } job)
            {
                return Results.NotFound();
            }

            var sensitive = await AllowedAsync(context, options, "payloads.read");
            var health = await diagnostics.HealthAsync(id, ct);
            if (context.RequestServices.GetService<IRuntimeQueries>() is { } runtime)
            {
                var readiness = await runtime.ReadReadinessAsync(id, cancellationToken: ct);
                health = new(readiness.Reason, readiness.SuspectedStalled, readiness.ObservedAt, health?.Progress);
            }

            if (!sensitive && health?.Progress is { } progress)
            {
                health = health with
                {
                    Progress = progress with
                    {
                        Description = null
                    }
                };
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
        }).WithPermission(options, "executions.read");
        group.MapGet("/api/flows/{id:guid}", async (Guid id, IJobQueries queries, IWorkflowDefinitions definitions, IJobDiagnostics diagnostics, CancellationToken ct) =>
        {
            if (await queries.GetWorkflowAsync(id, ct) is not { } flow)
            {
                return Results.NotFound();
            }

            var definition = await definitions.GetRunDefinitionAsync(id, ct);

            return Results.Json(new
            {
                flow = flow with
                {
                    Error = null
                },
                graph = definition is null ? null : Graph(definition),
                jobs = (await queries.ListFlowJobsAsync(id, ct)).Select(Metadata),
                events = await diagnostics.EventsAsync(id, cancellationToken: ct)
            }, JsonOptions);
        }).WithPermission(options, "executions.read");
        group.MapGet("/api/definitions", async (IWorkflowDefinitions definitions, CancellationToken ct) =>
            Results.Json((await definitions.DefinitionsAsync(cancellationToken: ct)).Select(d => new { d.Definition.Key, d.Definition.Version, d.Status, d.Revision, graph = Graph(d.Definition) }), JsonOptions))
            .WithPermission(options, "executions.read");
        group.MapGet("/api/workers", async (IJobDiagnostics diagnostics, CancellationToken ct) => Results.Json(await diagnostics.WorkersAsync(ct), JsonOptions))
            .WithPermission(options, "executions.read");
        group.MapGet("/api/deployment", async (IDeploymentQueries queries, IJobDiagnostics diagnostics, CancellationToken ct) =>
        {
            var requirements = await queries.DeploymentRequirementsAsync(ct);
            var workers = (await diagnostics.WorkersAsync(ct)).Where(w => w.SeenAt > requirements.ObservedAt.AddSeconds(-60))
                .Select(w => new WorkerCapability(w.WorkerId, w.Processors, w.Queues)).ToArray();

            return Results.Json(DeploymentPreflight.Compare(requirements, workers), JsonOptions);
        }).WithPermission(options, "executions.read");
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
            .WithPermission(options, "executions.read");
        group.MapGet("/api/schedules/{key}/occurrences", async (string key, IJobSchedules schedules, CancellationToken ct) => Results.Json(await schedules.OccurrencesAsync(key, ct), JsonOptions))
            .WithPermission(options, "executions.read");
        group.MapPost("/api/schedules/draft", async (ScheduleDraftCommand draft, HttpContext context, IJobSchedules schedules, CancellationToken ct) =>
            Results.Json(await schedules.SaveScheduleDraftAsync(draft.Definition, draft.Command.ExpectedRevision, Command(context, options, draft.Command), ct)))
            .Mutation(options, "schedules.manage");
        group.MapPost("/api/schedules/{key}/{version:int}/publish", async (string key, int version, DashboardCommand command, HttpContext context, IJobSchedules schedules, CancellationToken ct) =>
            Results.Json(await schedules.PublishScheduleAsync(key, version, command.ExpectedRevision, Command(context, options, command), ct)))
            .Mutation(options, "schedules.manage");
        foreach (var enable in new[] { true, false })
        {
            group.MapPost(enable ? "/api/schedules/{key}/{version:int}/enable" : "/api/schedules/{key}/{version:int}/pause", async (string key, int version, DashboardCommand command, HttpContext context, IJobSchedules schedules, CancellationToken ct) =>
                Results.Json(await schedules.SetScheduleEnabledAsync(key, version, command.ExpectedRevision, enable, Command(context, options, command), ct)))
                .Mutation(options, "schedules.manage");
        }

        group.MapGet("/api/audit", async (IJobAdministration administration, CancellationToken ct) => Results.Json(await administration.AuditAsync(cancellationToken: ct), JsonOptions))
            .WithPermission(options, "audit.read");
        group.MapPost("/api/jobs/{id:guid}/retry", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            Results.Json(await administration.RetryJobAsync(id, command.ExpectedAttempt, Command(context, options, command), ct)))
            .Mutation(options, "jobs.retry");
        group.MapPost("/api/jobs/{id:guid}/cancel", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            Results.Json(await administration.CancelJobAsync(id, command.ExpectedAttempt, Command(context, options, command), ct)))
            .Mutation(options, "jobs.cancel");
        group.MapPost("/api/jobs/{id:guid}/recover", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            Results.Json(await administration.RecoverStepAsync(id, command.ExpectedAttempt, Command(context, options, command), ct)))
            .Mutation(options, "flows.recover");
        foreach (var pause in new[] { true, false })
        {
            group.MapPost(pause ? "/api/flows/{id:guid}/pause" : "/api/flows/{id:guid}/resume", async (Guid id, DashboardCommand command, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
                Results.Json(await administration.SetFlowPausedAsync(id, pause, Command(context, options, command), ct)))
                .Mutation(options, "flows.recover");
        }

        group.MapPost("/api/definitions/draft", async (DraftCommand draft, HttpContext context, IWorkflowDefinitions definitions, CancellationToken ct) =>
            Results.Json(await definitions.SaveDraftAsync(draft.Definition, draft.Command.ExpectedRevision, Command(context, options, draft.Command), ct)))
            .Mutation(options, "definitions.edit");
        group.MapPost("/api/definitions/{key}/{version:int}/publish", async (string key, int version, DashboardCommand command, HttpContext context, IWorkflowDefinitions definitions, CancellationToken ct) =>
            Results.Json(await definitions.PublishAsync(key, version, command.ExpectedRevision, Command(context, options, command), ct)))
            .Mutation(options, "definitions.publish");
        group.MapPost("/api/definitions/{key}/{version:int}/retire", async (string key, int version, DashboardCommand command, HttpContext context, IWorkflowDefinitions definitions, CancellationToken ct) =>
            Results.Json(await definitions.RetireAsync(key, version, command.ExpectedRevision, Command(context, options, command), ct)))
            .Mutation(options, "definitions.retire");
        group.MapPost("/api/limits", async (LimitCommand request, HttpContext context, IJobAdministration administration, CancellationToken ct) =>
            Results.Json(await administration.ChangeLimitAsync(request.Limit, Command(context, options, request.Command), ct)))
            .Mutation(options, "limits.manage");
        MapOperations(group, options);
        MapExecutionQueries(group, options);
        MapRetention(group, options);

        return group;
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
        job.CausationJobId
    };
    private static object Graph(FlowDefinition definition) => new
    {
        definition.Key,
        definition.Version,
        definition.FailurePolicy,
        steps = definition.Steps.Select(s => new { s.Id, s.DisplayName, s.Contract, s.After, s.Queue, signal = s.Signal, conditional = s.Condition is not null })
    };
    private static async Task<bool> ValidTokenAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);

            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
