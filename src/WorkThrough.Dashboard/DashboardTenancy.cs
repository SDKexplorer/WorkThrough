using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

/// <summary>Tenant isolation (ADR-019): endpoints serve tenant-scoped principals only when marked, so new endpoints are store-wide by default.</summary>
public static partial class DashboardEndpoints
{
    private const string TenantAccessItem = "WorkThrough.TenantAccess";
    private const string StoreWideRequired = "This operation reads or changes every tenant's data and requires store-wide access.";
    private static readonly TenantCapableEndpoint TenantCapable = new();

    private sealed class TenantCapableEndpoint;

    private static RouteHandlerBuilder ForTenants(this RouteHandlerBuilder route) => route.WithMetadata(TenantCapable);

    private static void RequireStoreWideUnlessMarked(RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (context.GetEndpoint()?.Metadata.GetMetadata<TenantCapableEndpoint>() is null && (await TenantAccessAsync(context)).IsRestricted)
            {
                return Results.Json(new { error = StoreWideRequired }, statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(invocation);
        });

    /// <summary>Resolved once per request from the host's trusted identity; without a resolver every principal is store-wide.</summary>
    private static async Task<TenantAccess> TenantAccessAsync(HttpContext context)
    {
        if (context.Items[TenantAccessItem] is TenantAccess cached)
        {
            return cached;
        }

        var resolver = context.RequestServices.GetService<ITenantAccessResolver>();
        var access = resolver is null ? TenantAccess.AllTenants : await resolver.ResolveAsync(context.User, context.RequestAborted);
        context.Items[TenantAccessItem] = access;

        return access;
    }

    /// <summary>Runs an execution command only when the principal can see the target; otherwise it looks absent.</summary>
    private static async Task<IResult> ForVisibleJobAsync(HttpContext context, Guid id, Func<Task<IResult>> action)
    {
        var access = await TenantAccessAsync(context);
        if (access.IsRestricted)
        {
            var job = await context.RequestServices.GetRequiredService<IJobQueries>().GetAsync(id, context.RequestAborted);
            if (job is null || !access.Allows(job.TenantId))
            {
                return Results.NotFound();
            }
        }

        return await action();
    }

    /// <summary>Every step of a run carries the run's tenant.</summary>
    private static async Task<IResult> ForVisibleFlowAsync(HttpContext context, Guid runId, Func<Task<IResult>> action)
    {
        var access = await TenantAccessAsync(context);
        if (access.IsRestricted)
        {
            var steps = await context.RequestServices.GetRequiredService<IJobQueries>().ListFlowJobsAsync(runId, context.RequestAborted);
            if (steps.Count == 0 || !access.Allows(steps[0].TenantId))
            {
                return Results.NotFound();
            }
        }

        return await action();
    }
}
