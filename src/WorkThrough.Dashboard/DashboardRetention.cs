using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace WorkThrough;

public sealed record RetentionPolicyCommand(RetentionPolicy Policy, DashboardCommand Command);
public sealed record RetentionHoldCommand(bool Held, DashboardCommand Command);

public static partial class DashboardEndpoints
{
    private static void MapRetention(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/retention", ([FromServices] IRetentionQueries? queries, CancellationToken ct) =>
            ExecutionQueryResultAsync(queries, service => service.ReadRetentionStatusAsync(ct), ct))
            .WithPermission(options, "executions.read").WithPermission(options, "retention.read");
        group.MapGet("/api/retention/holds/{id:guid}", (Guid id, [FromServices] IRetentionQueries? queries, CancellationToken ct) =>
            ExecutionQueryResultAsync(queries, service => service.FindRetentionHoldAsync(id, ct), ct))
            .WithPermission(options, "executions.read").WithPermission(options, "retention.read");
        group.MapPost("/api/retention/policy", async (RetentionPolicyCommand request, HttpContext context,
            [FromServices] IRetentionAdministration? retention, CancellationToken ct) =>
            retention is null ? Results.Problem("Retention administration is unavailable.", statusCode: 503)
                : Results.Json(await retention.ConfigureRetentionAsync(request.Policy, request.Command.ExpectedRevision, Command(context, options, request.Command), ct)))
            .Mutation(options, "retention.manage");
        group.MapPost("/api/retention/holds/{id:guid}", async (Guid id, RetentionHoldCommand request, HttpContext context,
            [FromServices] IRetentionAdministration? retention, CancellationToken ct) =>
            retention is null ? Results.Problem("Retention administration is unavailable.", statusCode: 503)
                : Results.Json(await retention.SetRetentionHoldAsync(id, request.Held, request.Command.ExpectedRevision, Command(context, options, request.Command), ct), JsonOptions))
            .Mutation(options, "retention.manage");
        group.MapPost("/api/retention/incidents/{id:guid}/retry", async (Guid id, DashboardCommand request, HttpContext context,
            [FromServices] IRetentionAdministration? retention, CancellationToken ct) =>
            retention is null ? Results.Problem("Retention administration is unavailable.", statusCode: 503)
                : Results.Json(await retention.RetryRetentionIncidentAsync(id, Command(context, options, request), ct)))
            .Mutation(options, "retention.manage");
    }
}
