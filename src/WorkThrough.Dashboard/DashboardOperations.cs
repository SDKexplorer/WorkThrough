using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace WorkThrough;

public static partial class DashboardEndpoints
{
    private static void MapOperations(RouteGroupBuilder group, JobDashboardOptions options)
    {
        group.MapGet("/api/controls", async (IExecutionOperations operations, CancellationToken ct) =>
            Results.Json(new
            {
                queues = await operations.QueuesAsync(ct),
                workers = await operations.WorkerControlsAsync(ct)
            }, JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapPost("/api/queues", async (QueueControlCommand request, HttpContext context, IExecutionOperations operations, CancellationToken ct) =>
            ToHttp(await operations.SetQueuePausedAsync(request.Queue, request.Paused, request.Command.ExpectedRevision, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageQueues);
        group.MapPost("/api/queues/weight", async (QueueWeightCommand request, HttpContext context, IExecutionOperations operations, CancellationToken ct) =>
            ToHttp(await operations.SetQueueWeightAsync(request.Queue, request.Weight, request.Command.ExpectedRevision, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageQueues);
        group.MapPost("/api/workers", async (WorkerControlCommand request, HttpContext context, IExecutionOperations operations, CancellationToken ct) =>
            ToHttp(await operations.SetWorkerDrainingAsync(request.WorkerId, request.Draining, request.Command.ExpectedRevision, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageWorkers);
        group.MapPost("/api/jobs/{id:guid}/replay", async (Guid id, DashboardCommand request, HttpContext context, IExecutionOperations operations, CancellationToken ct) =>
            ToHttp(await operations.ReplayAsync(id, Command(context, options, request), ct)))
            .Mutation(options, OperationPermissions.ReplayJobs);
        group.MapGet("/api/archives", async (string? correlationId, IExecutionArchives archives, CancellationToken ct) =>
            Results.Json(await archives.ArchivesAsync(correlationId, cancellationToken: ct), JsonOptions))
            .WithPermission(options, OperationPermissions.ReadExecutions);
        group.MapGet("/api/archives/preview", async (DateTimeOffset succeededBefore, DateTimeOffset? failedBefore, IExecutionArchives archives, CancellationToken ct) =>
            Results.Json(await archives.ArchiveCandidatesAsync(new(succeededBefore, failedBefore), ct), JsonOptions))
            .WithPermission(options, OperationPermissions.ManageArchives);
        group.MapPost("/api/archives", async (ArchiveCommand request, HttpContext context, IExecutionArchives archives, CancellationToken ct) =>
            ToHttp(await archives.ArchiveCompletedAsync(request.Policy, Command(context, options, request.Command), ct)))
            .Mutation(options, OperationPermissions.ManageArchives);
        group.MapGet("/api/archives/{id:guid}/export", async (Guid id, IExecutionArchives archives, CancellationToken ct) =>
        {
            if (await archives.ExportArchiveAsync(id, ct) is not { } archive)
            {
                return Results.NotFound();
            }

            return Results.File(Encoding.UTF8.GetBytes(archive.Json), "application/json", $"workthrough-{id}.json");
        }).WithPermission(options, OperationPermissions.ReadExecutions).WithPermission(options, OperationPermissions.ReadPayloads);
        group.MapGet("/api/processors/registry", async (IProcessorRegistry registry, CancellationToken ct) =>
            Results.Json(await registry.ProcessorRegistryAsync(ct), JsonOptions)).WithPermission(options, OperationPermissions.ReadExecutions);
    }
}
