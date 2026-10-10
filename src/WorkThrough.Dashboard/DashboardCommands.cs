namespace WorkThrough;

public sealed record DashboardCommand(Guid CommandId, string Reason, int ExpectedAttempt = 0, int ExpectedRevision = 0);

public sealed record DraftCommand(FlowDefinition Definition, DashboardCommand Command);

public sealed record LimitCommand(ConcurrencyLimit Limit, DashboardCommand Command);

public sealed record ScheduleDraftCommand(ScheduleDefinition Definition, DashboardCommand Command);

public sealed record QueueControlCommand(string Queue, bool Paused, DashboardCommand Command);

public sealed record WorkerControlCommand(string WorkerId, bool Draining, DashboardCommand Command);

public sealed record ArchiveCommand(ArchivePolicy Policy, DashboardCommand Command);

public sealed record RetentionPolicyCommand(RetentionPolicy Policy, DashboardCommand Command);

public sealed record RetentionHoldCommand(bool Held, DashboardCommand Command);

public sealed record PartitionLimitCommand(PartitionLimit Limit, DashboardCommand Command);

public sealed record RateLimitCommand(RateLimit Limit, DashboardCommand Command);

/// <summary>Removes a partition limit, or a rate limit identified by its key and partitioning.</summary>
public sealed record PolicyRemovalCommand(string? JobKey, DashboardCommand Command, bool PerPartition = false);

public sealed record QueueWeightCommand(string Queue, int Weight, DashboardCommand Command);
