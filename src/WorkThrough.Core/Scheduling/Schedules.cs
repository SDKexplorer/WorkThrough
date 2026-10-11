using System.Text.Json;
using Cronos;

namespace WorkThrough;

public enum ScheduleOverlap
{
    Allow, SkipIfActive, Coalesce
}

public enum ScheduleMisfire
{
    Skip, Coalesce, CatchUp
}

public sealed record ScheduleDefinition(string Key, int Version, string Cron, string TimeZone, JobContractId Processor,
    JsonElement Input, string Queue = JobDefaults.Queue, int MaxAttempts = JobDefaults.MaxAttempts, ScheduleOverlap Overlap = ScheduleOverlap.SkipIfActive,
    ScheduleMisfire Misfire = ScheduleMisfire.Coalesce, string? CorrelationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? PartitionKey = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);
public sealed record StoredSchedule(ScheduleDefinition Definition, DefinitionStatus Status, int Revision, bool Enabled, DateTimeOffset? NextOccurrence);

/// <summary>Outcome is one of <see cref="ScheduleOccurrenceOutcomes"/>.</summary>
public sealed record ScheduleOccurrence(string Key, int Version, DateTimeOffset Due, string Outcome, Guid? JobId);

public static class ScheduleOccurrenceOutcomes
{
    public const string Dispatched = "Dispatched";
    public const string MisfireDispatched = "MisfireDispatched";
    public const string SkippedActive = "SkippedActive";
    public const string SkippedMisfire = "SkippedMisfire";
}

/// <summary>Five-field cron expressions in an explicit time zone. Persist UTC occurrence instants.</summary>
public static class ScheduleClock
{
    /// <summary>An occurrence dispatched later than this after its due time is a misfire.</summary>
    public static TimeSpan MisfireThreshold { get; } = TimeSpan.FromSeconds(30);

    public static void Validate(ScheduleDefinition definition)
    {
        _ = new JobContractId(definition.Key, definition.Version);
        ArgumentNullException.ThrowIfNull(definition.Processor);
        if (definition.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || string.IsNullOrWhiteSpace(definition.Queue)
            || definition.MaxAttempts < 1 || !Enum.IsDefined(definition.Overlap) || !Enum.IsDefined(definition.Misfire))
        {
            throw new ArgumentException("Invalid schedule input, queue, retry count, or policy.");
        }

        _ = Next(definition, DateTimeOffset.UtcNow);
    }

    public static DateTimeOffset? Next(ScheduleDefinition definition, DateTimeOffset after)
    {
        try
        {
            return CronExpression.Parse(definition.Cron).GetNextOccurrence(after, TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZone));
        }
        catch (Exception exception) when (exception is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("The schedule requires a valid five-field cron expression and installed time zone.", exception);
        }
    }
}

/// <summary>Lifecycle changes return the new revision.</summary>
public interface IJobSchedules
{
    Task<Result<int>> SaveScheduleDraftAsync(ScheduleDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> PublishScheduleAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> SetScheduleEnabledAsync(string key, int version, int expectedRevision, bool enabled, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleOccurrence>> OccurrencesAsync(string key, CancellationToken cancellationToken = default);
    Task<int> DispatchDueSchedulesAsync(int batchSize = 100, CancellationToken cancellationToken = default);
}
