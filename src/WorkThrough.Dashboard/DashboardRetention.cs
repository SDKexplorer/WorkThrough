using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace WorkThrough;

public sealed record RetentionPolicyCommand(RetentionPolicy Policy, DashboardCommand Command);

public sealed record RetentionHoldCommand(bool Held, DashboardCommand Command);

public static partial class DashboardEndpoints
{
    private const string RetentionUnavailable = "Retention administration is unavailable.";

    private static void MapRetention(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/retention", ([FromServices] IRetentionQueries? queries, CancellationToken ct) =>
            ExecutionQueryResultAsync(queries, service => service.ReadRetentionStatusAsync(ct), ct))
            .WithPermission(options, OperationPermissions.ReadExecutions).WithPermission(options, OperationPermissions.ReadRetention);
        group.MapGet("/api/retention/holds/{id:guid}", (Guid id, [FromServices] IRetentionQueries? queries, CancellationToken ct) =>
            ExecutionQueryResultAsync(queries, service => service.FindRetentionHoldAsync(id, ct), ct))
            .WithPermission(options, OperationPermissions.ReadExecutions).WithPermission(options, OperationPermissions.ReadRetention);
        group.MapPost("/api/retention/policy", async (RetentionPolicyCommand request, HttpContext context,
            [FromServices] IRetentionAdministration? retention, CancellationToken ct) =>
            retention is null ? Unavailable(RetentionUnavailable)
                : ToHttp(await retention.ConfigureRetentionAsync(request.Policy, request.Command.ExpectedRevision, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageRetention);
        group.MapPost("/api/retention/holds/{id:guid}", async (Guid id, RetentionHoldCommand request, HttpContext context,
            [FromServices] IRetentionAdministration? retention, CancellationToken ct) =>
            retention is null ? Unavailable(RetentionUnavailable)
                : ToHttp(await retention.SetRetentionHoldAsync(id, request.Held, request.Command.ExpectedRevision, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageRetention);
        group.MapPost("/api/retention/incidents/{id:guid}/retry", async (Guid id, DashboardCommand request, HttpContext context,
            [FromServices] IRetentionAdministration? retention, CancellationToken ct) =>
            retention is null ? Unavailable(RetentionUnavailable)
                : ToHttp(await retention.RetryRetentionIncidentAsync(id, Command(context, options, request), ct)))
            .Mutation(options, OperationPermissions.ManageRetention);
    }
}
