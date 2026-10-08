using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;

namespace WorkThrough;

public static partial class DashboardEndpoints
{
    private static void MapExecutionQueries(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/executions", SearchExecutionMetadataAsync).WithPermission(options, "executions.read");
        group.MapGet("/api/executions/{id:guid}", FindExecutionMetadataAsync).WithPermission(options, "executions.read");
        group.MapGet("/api/executions/{id:guid}/attempts", ReadExecutionAttemptsAsync).WithPermission(options, "executions.read");
        group.MapGet("/api/metrics", ReadRuntimeMetricsAsync).WithPermission(options, "executions.read");
        group.MapGet("/api/executions/{id:guid}/readiness", ReadExecutionReadinessAsync).WithPermission(options, "executions.read");
    }

    private static Task<IResult> SearchExecutionMetadataAsync([AsParameters] ExecutionQuery query,
        [FromServices] IExecutionQueries? queries, CancellationToken ct) =>
        ExecutionQueryResultAsync(queries, service => service.SearchExecutionsAsync(query, ct), ct);

    private static Task<IResult> FindExecutionMetadataAsync(Guid id, [FromServices] IExecutionQueries? queries, CancellationToken ct) =>
        ExecutionQueryResultAsync(queries, service => service.FindExecutionAsync(id, ct), ct);

    private static Task<IResult> ReadExecutionAttemptsAsync(Guid id, int? afterAttempt, int? limit,
        [FromServices] IExecutionQueries? queries, CancellationToken ct) =>
        ExecutionQueryResultAsync(queries, service => service.ReadAttemptsAsync(id, afterAttempt ?? 0, limit ?? 25, ct), ct);

    private static Task<IResult> ReadRuntimeMetricsAsync([AsParameters] RuntimeMetricsQuery query,
        [FromServices] IRuntimeQueries? queries, CancellationToken ct) =>
        ExecutionQueryResultAsync(queries, service => service.ReadRuntimeMetricsAsync(query, ct), ct);

    private static Task<IResult> ReadExecutionReadinessAsync(Guid id, [FromServices] IRuntimeQueries? queries, CancellationToken ct) =>
        ExecutionQueryResultAsync(queries, service => service.ReadReadinessAsync(id, cancellationToken: ct), ct);

    private static async Task<IResult> ExecutionQueryResultAsync<TService, TResult>(TService? queries,
        Func<TService, Task<TResult>> read, CancellationToken ct) where TService : class
    {
        if (queries is null)
        {
            return Results.Problem("This store does not provide bounded execution queries.", statusCode: StatusCodes.Status503ServiceUnavailable);
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
            return Results.Problem("The execution query exceeded its time budget.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
