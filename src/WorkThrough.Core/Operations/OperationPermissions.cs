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
    public const string RetryJobs = "jobs.retry";
    public const string CancelJobs = "jobs.cancel";
    public const string ReplayJobs = "jobs.replay";
    public const string RecoverFlows = "flows.recover";
    public const string EditDefinitions = "definitions.edit";
    public const string PublishDefinitions = "definitions.publish";
    public const string RetireDefinitions = "definitions.retire";
    public const string ManageSchedules = "schedules.manage";
    public const string ManageLimits = "limits.manage";
    public const string ManageRetention = "retention.manage";
    public const string ManageQueues = "queues.manage";
    public const string ManageWorkers = "workers.manage";
    public const string ManageArchives = "archives.manage";

    /// <summary>Replaces the input of a failed job or step and retries it. Granted separately because it changes business data.</summary>
    public const string CorrectInputs = "inputs.correct";

    public static IReadOnlyList<string> All { get; } =
    [
        ReadExecutions, ReadPayloads, RetryJobs, CancelJobs, RecoverFlows, EditDefinitions, PublishDefinitions, RetireDefinitions,
        ManageSchedules, ManageLimits, ReadAudit, ReadRetention, ManageRetention, ManageQueues, ManageWorkers, ReplayJobs, ManageArchives,
        CorrectInputs
    ];

    public static string Policy(string permission) => PolicyPrefix + permission;
}

/// <summary>Audit action names recorded with each administrative command. Use them to filter audit history.</summary>
public static class AuditActions
{
    public const string RetryJob = "jobs.retry";
    public const string CancelJob = "jobs.cancel";
    public const string ReplayJob = "jobs.replay";
    public const string RecoverStep = "flows.recover";
    public const string CorrectInput = "jobs.correct-input";
    public const string ForkRun = "flows.fork";
    public const string PauseFlow = "flows.pause";
    public const string ResumeFlow = "flows.resume";
    public const string ChangeLimit = "limits.manage";
    public const string SetKeyedLimit = "limits.keyed.set";
    public const string RemoveKeyedLimit = "limits.keyed.remove";
    public const string SetRateLimit = "limits.rate.set";
    public const string RemoveRateLimit = "limits.rate.remove";
    public const string EditDefinition = "definitions.edit";
    public const string PublishDefinition = "definitions.publish";
    public const string RetireDefinition = "definitions.retire";
    public const string EditSchedule = "schedules.edit";
    public const string PublishSchedule = "schedules.publish";
    public const string EnableSchedule = "schedules.enable";
    public const string PauseSchedule = "schedules.pause";
    public const string SaveCalendar = "calendars.save";
    public const string DeleteCalendar = "calendars.delete";
    public const string PauseQueue = "queues.pause";
    public const string ResumeQueue = "queues.resume";
    public const string SetQueueWeight = "queues.weight";
    public const string DrainWorker = "workers.drain";
    public const string ResumeWorker = "workers.resume";
    public const string CreateArchives = "archives.create";
    public const string PurgeArchive = "archives.purge";
    public const string TrimEvents = "events.trim";
    public const string SetProcessorAliases = "processors.aliases";
    public const string ConfigureRetention = "retention.configure";
    public const string SetRetentionHold = "retention.hold";
    public const string RetryRetentionIncident = "retention.retry";
    public const string RunRetentionBatch = "retention.batch";
}

/// <summary>A host-configured store endpoint and optional execution target. Correlation is never an authorization boundary.</summary>
public sealed record OperationResource(string Endpoint, string Environment, Guid? ExecutionId = null)
{
    /// <summary>Saved flow target for graph and event queries; absent for store-wide and individual-execution reads.</summary>
    public Guid? WorkflowRunId { get; init; }

    /// <summary>Archive summary target; this is a standalone job ID or whole-flow run ID.</summary>
    public Guid? ArchiveId { get; init; }
}
