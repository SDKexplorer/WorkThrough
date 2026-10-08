namespace WorkThrough;

/// <summary>Permission names shared by operations transports; hosts own their authorization policies.</summary>
public static class OperationPermissions
{
    public const string ClaimType = "workthrough.permission";
    public const string PolicyPrefix = "WorkThrough.";
    public const string ReadExecutions = "executions.read";
    public static IReadOnlyList<string> All { get; } = [ReadExecutions, "payloads.read", "jobs.retry", "jobs.cancel", "flows.recover",
        "definitions.edit", "definitions.publish", "definitions.retire", "schedules.manage", "limits.manage", "audit.read", "queues.manage", "workers.manage", "jobs.replay", "archives.manage"];
}

/// <summary>A host-configured store endpoint and optional execution target. Correlation is never an authorization boundary.</summary>
public sealed record OperationResource(string Endpoint, string Environment, Guid? ExecutionId = null);
