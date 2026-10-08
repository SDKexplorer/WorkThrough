using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

public static class DashboardPermissions
{
    public static IReadOnlyList<string> All { get; } = ["executions.read", "payloads.read", "jobs.retry", "jobs.cancel", "flows.recover",
        "definitions.edit", "definitions.publish", "definitions.retire", "schedules.manage", "limits.manage", "audit.read", "queues.manage", "workers.manage", "jobs.replay", "archives.manage"];
    public const string ClaimType = "workthrough.permission";
    public const string PolicyPrefix = "WorkThrough.";
}

/// <summary>Host policies may authorize a configured store or an individual route resource. Correlation is not an access boundary.</summary>
public sealed record DashboardResource(string Path, IReadOnlyDictionary<string, object?> RouteValues);
public sealed record DashboardCommand(Guid CommandId, string Reason, int ExpectedAttempt = 0, int ExpectedRevision = 0);
public sealed record DraftCommand(FlowDefinition Definition, DashboardCommand Command);
public sealed record LimitCommand(ConcurrencyLimit Limit, DashboardCommand Command);
public sealed record ScheduleDraftCommand(ScheduleDefinition Definition, DashboardCommand Command);

public static partial class DashboardEndpoints
{
    private static Task<bool> AllowedAsync(HttpContext context, JobDashboardOptions options, string permission)
    {
        if (options.AllowAnonymous)
        {
            return Task.FromResult(true);
        }

        return AuthorizeAsync();
        async Task<bool> AuthorizeAsync() => (await context.RequestServices.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(context.User, new DashboardResource(context.Request.Path, new Dictionary<string, object?>(context.Request.RouteValues)),
                DashboardPermissions.PolicyPrefix + permission)).Succeeded;
    }

    private static RouteHandlerBuilder WithPermission(this RouteHandlerBuilder route, JobDashboardOptions options, string permission) =>
        route.AddEndpointFilter(async (invocation, next) =>
            await AllowedAsync(invocation.HttpContext, options, permission) ? await next(invocation) : Results.Forbid());

    private static OperationActor Actor(HttpContext context, JobDashboardOptions options)
    {
        if (options.AllowAnonymous && context.User.Identity?.IsAuthenticated != true)
        {
            return new("workthrough-development", "anonymous", "Development user");
        }

        var subject = context.User.FindFirst("sub") ?? context.User.FindFirst(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Dashboard operators need a trusted sub or NameIdentifier claim.");

        return new(context.User.FindFirst("iss")?.Value ?? subject.Issuer, subject.Value, context.User.Identity?.Name);
    }

    private static AdministrativeCommand Command(HttpContext context, JobDashboardOptions options, DashboardCommand command) =>
        new(command.CommandId, Actor(context, options), command.Reason);

    private static RouteHandlerBuilder Mutation(this RouteHandlerBuilder route, JobDashboardOptions options, string permission) =>
        route.WithPermission(options, permission).AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (!await ValidTokenAsync(context, context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()))
            {
                return Results.BadRequest(new
                {
                    error = "Invalid anti-forgery token."
                });
            }

            try
            {
                return await next(invocation);
            }
            catch (StateConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
}
