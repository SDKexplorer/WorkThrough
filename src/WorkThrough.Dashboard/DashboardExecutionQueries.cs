using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;

namespace WorkThrough;

public static partial class DashboardEndpoints
{
    private static void MapExecutionQueries(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/executions", SearchExecutionMetadataAsync).WithPermission(options, OperationPermissions.ReadExecutions).ForTenants();
        group.MapGet("/api/executions/{id:guid}", FindExecutionMetadataAsync).WithPermission(options, OperationPermissions.ReadExecutions).ForTenants();
        group.MapGet("/api/executions/{id:guid}/attempts", ReadExecutionAttemptsAsync).WithPermission(options, OperationPermissions.ReadExecutions).ForTenants();
        group.MapGet("/api/metrics", ReadRuntimeMetricsAsync).WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/executions/{id:guid}/readiness", ReadExecutionReadinessAsync).WithPermission(options, OperationPermissions.ReadExecutions).ForTenants();
    }

    private static async Task<IResult> SearchExecutionMetadataAsync([AsParameters] ExecutionQuery query, HttpContext context,
        [FromServices] IExecutionQueries? queries, CancellationToken ct)
    {
        var access = await TenantAccessAsync(context);

        return await ExecutionQueryResultAsync(queries, service => service.ScopedTo(access).SearchExecutionsAsync(query, ct), ct);
    }

    private static async Task<IResult> FindExecutionMetadataAsync(Guid id, HttpContext context, [FromServices] IExecutionQueries? queries, CancellationToken ct)
    {
        var access = await TenantAccessAsync(context);

        return await ExecutionQueryResultAsync(queries, service => service.ScopedTo(access).FindExecutionAsync(id, ct), ct);
    }

    private static async Task<IResult> ReadExecutionAttemptsAsync(Guid id, int? afterAttempt, int? limit, HttpContext context,
        [FromServices] IExecutionQueries? queries, CancellationToken ct)
    {
        var access = await TenantAccessAsync(context);

        return await ExecutionQueryResultAsync(queries, service =>
            service.ScopedTo(access).ReadAttemptsAsync(id, afterAttempt ?? 0, limit ?? OperationQueryLimits.DefaultPageSize, ct), ct);
    }

    private static Task<IResult> ReadRuntimeMetricsAsync([AsParameters] RuntimeMetricsQuery query,
        [FromServices] IRuntimeQueries? queries, CancellationToken ct) =>
        ExecutionQueryResultAsync(queries, service => service.ReadRuntimeMetricsAsync(query, ct), ct);

    /// <summary>Readiness of a tenant-scoped principal's own work keeps the reason without other tenants' load.</summary>
    private static async Task<IResult> ReadExecutionReadinessAsync(Guid id, HttpContext context, [FromServices] IRuntimeQueries? queries,
        [FromServices] IExecutionQueries? executions, CancellationToken ct)
    {
        var access = await TenantAccessAsync(context);
        if (access.IsRestricted && executions is null)
        {
            return Unavailable("Tenant-scoped readiness requires bounded execution queries.");
        }

        return await ExecutionQueryResultAsync(queries, service =>
            (access.IsRestricted ? service.ScopedTo(executions!, access) : service).ReadReadinessAsync(id, cancellationToken: ct), ct);
    }

    private static async Task<IResult> ExecutionQueryResultAsync<TService, TResult>(TService? queries,
        Func<TService, Task<TResult>> read, CancellationToken ct) where TService : class
    {
        if (queries is null)
        {
            return Unavailable("This store does not provide bounded execution queries.");
        }

        try
        {
            return Results.Json(await read(queries), JsonOptions);
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("The execution query exceeded its time budget.");
        }
    }
}
