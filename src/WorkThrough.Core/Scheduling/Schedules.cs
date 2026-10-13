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
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null)
{
    public const string DefaultTimeZone = "UTC";

    /// <summary>
    /// Fires every interval, counted from <see cref="StartsAt"/> (or the Unix epoch), instead of on <see cref="Cron"/>, which is then empty.
    /// At least one second.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? Interval { get; init; }

    /// <summary>No occurrence before this instant.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>No occurrence after this instant; the schedule then has no next occurrence.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>Dates, in the schedule's time zone, on which occurrences are skipped: a holiday calendar.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DateOnly>? ExcludedDates { get; init; }

    /// <summary>This schedule, skipping occurrences on <paramref name="dates"/>.</summary>
    public ScheduleDefinition Excluding(params DateOnly[] dates) => this with { ExcludedDates = [.. ExcludedDates ?? [], .. dates] };

    /// <summary>This schedule, with no occurrence before <paramref name="instant"/>; an interval counts from it.</summary>
    public ScheduleDefinition Starting(DateTimeOffset instant) => this with { StartsAt = instant };

    /// <summary>This schedule, with no occurrence after <paramref name="instant"/>.</summary>
    public ScheduleDefinition Ending(DateTimeOffset instant) => this with { EndsAt = instant };

    /// <summary>The engine protocol every host must understand before this schedule may be saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RequiredProtocol => Interval is not null || StartsAt is not null || EndsAt is not null || ExcludedDates is { Count: > 0 }
        || ScheduleClock.HasSeconds(Cron) ? EngineProtocol.RichSchedules : EngineProtocol.Baseline;

    /// <summary>A schedule that invokes a typed processor definition every <paramref name="interval"/> with fixed input.</summary>
    public static ScheduleDefinition Every<TInput>(string key, TimeSpan interval, JobDefinition<TInput> processor, TInput input, int version = 1) =>
        Create(key, "", processor, input, DefaultTimeZone, version) with { Interval = interval };

    /// <summary>A schedule that invokes a typed processor definition with fixed input, in the definition's queue and attempt budget.</summary>
    public static ScheduleDefinition Create<TInput>(string key, string cron, JobDefinition<TInput> processor, TInput input,
        string timeZone = DefaultTimeZone, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(processor);

        return new(key, version, cron, timeZone, processor.Identity, JsonSerializer.SerializeToElement(input, JobJson.Options), processor.Queue,
            processor.MaxAttempts);
    }

    /// <summary>A schedule that enqueues fixed input at the address its type's [JobContract] declares.</summary>
    public static ScheduleDefinition Create<TInput>(string key, string cron, TInput input, string timeZone = DefaultTimeZone, int version = 1) =>
        new(key, version, cron, timeZone, JobContractId.For<TInput>(), JsonSerializer.SerializeToElement(input, JobJson.Options));
}
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

/// <summary>
/// Cron expressions (five fields, or six with seconds) or fixed intervals, in an explicit time zone, within optional bounds and
/// excluded dates. Persist UTC occurrence instants.
/// </summary>
public static class ScheduleClock
{
    /// <summary>The shortest interval: the scheduler dispatches once a second.</summary>
    public static TimeSpan MinimumInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Excluded occurrences skipped in a row before a schedule is treated as having no next occurrence.</summary>
    private const int MaximumSkippedOccurrences = 10_000;

    private const int CronFieldsWithSeconds = 6;

    /// <summary>True for a six-field cron expression, whose first field is seconds.</summary>
    public static bool HasSeconds(string cron) => cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == CronFieldsWithSeconds;

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
        TimeZoneInfo zone;
        CronExpression? cron = null;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZone);
            if (definition.Interval is null)
            {
                cron = CronExpression.Parse(definition.Cron, HasSeconds(definition.Cron) ? CronFormat.IncludeSeconds : CronFormat.Standard);
            }
        }
        catch (Exception exception) when (exception is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("The schedule requires a valid cron expression (five fields, or six with seconds) and an installed time zone.", exception);
        }

        if (definition.Interval is { } interval && (interval < MinimumInterval || !string.IsNullOrEmpty(definition.Cron)))
        {
            throw new ArgumentException($"An interval schedule needs an interval of at least {MinimumInterval.TotalSeconds:0} second and no cron expression.");
        }

        if (definition.EndsAt <= definition.StartsAt)
        {
            throw new ArgumentException("A schedule must end after it starts.");
        }

        var excluded = definition.ExcludedDates is { Count: > 0 } dates ? dates.ToHashSet() : null;
        var from = definition.StartsAt is { } start && start > after ? start.AddTicks(-1) : after;
        for (var skipped = 0; skipped < MaximumSkippedOccurrences; skipped++)
        {
            var next = cron is null ? NextInterval(definition.Interval!.Value, definition.StartsAt ?? DateTimeOffset.UnixEpoch, from)
                : cron.GetNextOccurrence(from, zone);
            if (next is null || next > definition.EndsAt)
            {
                return null;
            }

            if (excluded is null || !excluded.Contains(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next.Value, zone).DateTime)))
            {
                return next;
            }

            from = next.Value;
        }

        return null;
    }

    /// <summary>The first instant anchor + k × interval after <paramref name="after"/>.</summary>
    private static DateTimeOffset NextInterval(TimeSpan interval, DateTimeOffset anchor, DateTimeOffset after)
    {
        if (after < anchor)
        {
            return anchor;
        }

        var periods = (after - anchor).Ticks / interval.Ticks + 1;

        return anchor + TimeSpan.FromTicks(checked(periods * interval.Ticks));
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
