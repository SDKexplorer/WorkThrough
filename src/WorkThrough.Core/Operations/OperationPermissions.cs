namespace WorkThrough;

/// <summary>Permission names shared by operations transports; hosts own their authorization policies.</summary>
public static class OperationPermissions
{
    public const string ClaimType = "workthrough.permission";
    public const string PolicyPrefix = "WorkThrough.";
    public const string ReadExecutions = "executions.read";
    public const string ReadPayloads = "payloads.read";
    public const string ReadAudit = "audit.read";
    public const string ReadRetention = "retention.read";
    public const string ManageRetention = "retention.manage";
    public static IReadOnlyList<string> All { get; } = [ReadExecutions, ReadPayloads, "jobs.retry", "jobs.cancel", "flows.recover",
        "definitions.edit", "definitions.publish", "definitions.retire", "schedules.manage", "limits.manage", ReadAudit, ReadRetention, ManageRetention, "queues.manage", "workers.manage", "jobs.replay", "archives.manage"];
}

/// <summary>A host-configured store endpoint and optional execution target. Correlation is never an authorization boundary.</summary>
public sealed record OperationResource(string Endpoint, string Environment, Guid? ExecutionId = null)
{
    /// <summary>Saved flow target for graph and event queries; absent for store-wide and individual-execution reads.</summary>
    public Guid? WorkflowRunId { get; init; }
    /// <summary>Archive summary target; this is a standalone job ID or whole-flow run ID.</summary>
    public Guid? ArchiveId { get; init; }
}
