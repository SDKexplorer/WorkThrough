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

    /// <summary>Times this schedule skips, in its time zone.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScheduleCalendar? Calendar { get; init; }

    /// <summary>
    /// Named calendars whose times this schedule also skips. They are read when the schedule is published and each time it advances, so
    /// editing a calendar changes every schedule that names it without a new schedule version.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Calendars { get; init; }

    /// <summary>For an interval schedule, the daily time range, and optionally weekdays, its occurrences fall in.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DailyWindow? Window { get; init; }

    /// <summary>
    /// Fires every so many days, weeks, months or years at the local time of <see cref="StartsAt"/>, instead of on <see cref="Cron"/>,
    /// which is then empty.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CalendarInterval? CalendarInterval { get; init; }

    /// <summary>This schedule, skipping occurrences on <paramref name="dates"/>.</summary>
    public ScheduleDefinition Excluding(params DateOnly[] dates) => Excluding(new ScheduleCalendar().ExcludingDates(dates));

    /// <summary>This schedule, also skipping everything <paramref name="calendar"/> skips.</summary>
    public ScheduleDefinition Excluding(ScheduleCalendar calendar) => this with { Calendar = Calendar?.Combine(calendar) ?? calendar };

    /// <summary>This schedule, also skipping everything the named calendar <paramref name="name"/> skips when it fires.</summary>
    public ScheduleDefinition ExcludingCalendar(string name) => this with { Calendars = [.. (Calendars ?? []).Append(name).Distinct(StringComparer.Ordinal)] };

    /// <summary>This interval schedule, firing only from <paramref name="from"/> to <paramref name="until"/> each day, on <paramref name="days"/> if given.</summary>
    public ScheduleDefinition Within(TimeOnly from, TimeOnly until, params DayOfWeek[] days) =>
        this with { Window = new(from, until, days.Length == 0 ? null : days) };

    /// <summary>This schedule, with no occurrence before <paramref name="instant"/>; an interval counts from it.</summary>
    public ScheduleDefinition Starting(DateTimeOffset instant) => this with { StartsAt = instant };

    /// <summary>This schedule, with no occurrence after <paramref name="instant"/>.</summary>
    public ScheduleDefinition Ending(DateTimeOffset instant) => this with { EndsAt = instant };

    /// <summary>The engine protocol every host must understand before this schedule may be saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RequiredProtocol => Calendars is { Count: > 0 } ? EngineProtocol.NamedCalendars
        : Interval is not null || CalendarInterval is not null || StartsAt is not null || EndsAt is not null || Calendar is not null || Window is not null
            || ScheduleClock.HasSeconds(Cron) ? EngineProtocol.RichSchedules : EngineProtocol.Baseline;

    /// <summary>A schedule that invokes a typed processor definition every <paramref name="interval"/> with fixed input.</summary>
    public static ScheduleDefinition Every<TInput>(string key, TimeSpan interval, JobDefinition<TInput> processor, TInput input, int version = 1) =>
        Create(key, "", processor, input, DefaultTimeZone, version) with { Interval = interval };

    /// <summary>
    /// A schedule that invokes a typed processor definition every <paramref name="count"/> calendar units from <paramref name="first"/>,
    /// keeping its local time in <paramref name="timeZone"/>.
    /// </summary>
    public static ScheduleDefinition EveryCalendar<TInput>(string key, int count, CalendarUnit unit, DateTimeOffset first, JobDefinition<TInput> processor,
        TInput input, string timeZone = DefaultTimeZone, int version = 1) =>
        Create(key, "", processor, input, timeZone, version) with { CalendarInterval = new(count, unit), StartsAt = first };

    /// <summary>A schedule that invokes a typed processor definition with fixed input, in the definition's queue and attempt budget.</summary>
    public static ScheduleDefinition Create<TInput>(string key, string cron, JobDefinition<TInput> processor, TInput input,
        string timeZone = DefaultTimeZone, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(processor);

        return new(key, version, cron, timeZone, processor.Identity, JobJson.ToElement(input), processor.Queue,
            processor.MaxAttempts);
    }

    /// <summary>A schedule that enqueues fixed input at the address its type's [JobContract] declares.</summary>
    public static ScheduleDefinition Create<TInput>(string key, string cron, TInput input, string timeZone = DefaultTimeZone, int version = 1) =>
        new(key, version, cron, timeZone, JobContractId.For<TInput>(), JobJson.ToElement(input));
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
/// Occurrences of a schedule: a cron expression (five fields, or six with seconds), a fixed interval, optionally inside a daily window, or
/// a calendar interval, in an explicit time zone, within optional bounds and minus its calendar. Persist UTC occurrence instants.
/// </summary>
public static class ScheduleClock
{
    /// <summary>An occurrence dispatched later than this after its due time is a misfire.</summary>
    public static TimeSpan MisfireThreshold { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest interval: the scheduler dispatches once a second.</summary>
    public static TimeSpan MinimumInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Skipped candidates in a row before a schedule is treated as having no next occurrence.</summary>
    private const int MaximumSkippedOccurrences = 100_000;

    /// <summary>Days searched for a daily window's next included day; a week covers every weekday filter.</summary>
    private const int WindowSearchDays = 8;

    private const int CronFieldsWithSeconds = 6;

    /// <summary>True for a six-field cron expression, whose first field is seconds.</summary>
    public static bool HasSeconds(string cron) => cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == CronFieldsWithSeconds;

    internal static CronExpression ParseCron(string cron)
    {
        try
        {
            return CronExpression.Parse(cron, HasSeconds(cron) ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException exception)
        {
            throw new ArgumentException($"'{cron}' is not a valid cron expression (five fields, or six with seconds).", exception);
        }
    }

    public static void Validate(ScheduleDefinition definition)
    {
        _ = new JobContractId(definition.Key, definition.Version);
        ArgumentNullException.ThrowIfNull(definition.Processor);
        if (definition.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || string.IsNullOrWhiteSpace(definition.Queue)
            || definition.MaxAttempts < 1 || !Enum.IsDefined(definition.Overlap) || !Enum.IsDefined(definition.Misfire))
        {
            throw new ArgumentException("Invalid schedule input, queue, retry count, or policy.");
        }

        foreach (var name in definition.Calendars ?? [])
        {
            NamedCalendar.ValidateName(name);
        }

        _ = Next(definition, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <paramref name="definition"/> with the named calendars it references folded into its own calendar, so <see cref="Next"/> skips
    /// their times. Every referenced name must be present in <paramref name="calendars"/>.
    /// </summary>
    public static ScheduleDefinition WithCalendars(ScheduleDefinition definition, IReadOnlyDictionary<string, ScheduleCalendar> calendars)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(calendars);
        var calendar = definition.Calendar;
        foreach (var name in definition.Calendars ?? [])
        {
            var named = calendars.TryGetValue(name, out var found) ? found
                : throw new InvalidOperationException($"Schedule {definition.Key} names calendar '{name}', which does not exist.");
            calendar = calendar?.Combine(named) ?? named;
        }

        return definition with { Calendar = calendar, Calendars = null };
    }

    public static DateTimeOffset? Next(ScheduleDefinition definition, DateTimeOffset after)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Time zone '{definition.TimeZone}' is not installed.", exception);
        }

        var candidate = Candidates(definition, zone);
        definition.Calendar?.Validate();
        if (definition.EndsAt <= definition.StartsAt)
        {
            throw new ArgumentException("A schedule must end after it starts.");
        }

        var from = definition.StartsAt is { } start && start > after ? start.AddTicks(-1) : after;
        for (var skipped = 0; skipped < MaximumSkippedOccurrences; skipped++)
        {
            if (candidate(from) is not { } next || next > definition.EndsAt)
            {
                return null;
            }

            if (definition.Calendar?.SkippedUntil(next, zone) is not { } until)
            {
                return next;
            }

            var resume = ToInstant(until, zone).AddTicks(-1);
            from = resume > next ? resume : next;
        }

        return null;
    }

    /// <summary>The occurrence generator, before bounds and calendar: the first occurrence after a given instant.</summary>
    private static Func<DateTimeOffset, DateTimeOffset?> Candidates(ScheduleDefinition definition, TimeZoneInfo zone)
    {
        var kinds = (string.IsNullOrEmpty(definition.Cron) ? 0 : 1) + (definition.Interval is null ? 0 : 1) + (definition.CalendarInterval is null ? 0 : 1);
        if (kinds != 1)
        {
            throw new ArgumentException("A schedule needs exactly one of a cron expression, an interval or a calendar interval.");
        }

        if (definition.Window is not null && definition.Interval is null)
        {
            throw new ArgumentException("A daily window applies to an interval schedule.");
        }

        if (definition.Interval is { } interval)
        {
            if (interval < MinimumInterval)
            {
                throw new ArgumentException($"An interval schedule needs an interval of at least {MinimumInterval.TotalSeconds:0} second.");
            }

            if (definition.Window is { } window)
            {
                if (window.From >= window.Until || window.Days is { Count: 0 })
                {
                    throw new ArgumentException("A daily window must start before it ends, on at least one day.");
                }

                return after => NextInWindow(interval, window, zone, after);
            }

            var anchor = definition.StartsAt ?? DateTimeOffset.UnixEpoch;

            return after => NextInterval(interval, anchor, after);
        }

        if (definition.CalendarInterval is { } calendar)
        {
            if (calendar.Count < 1 || !Enum.IsDefined(calendar.Unit) || definition.StartsAt is null)
            {
                throw new ArgumentException("A calendar interval needs a positive count, a unit and a start.");
            }

            var first = TimeZoneInfo.ConvertTime(definition.StartsAt.Value, zone).DateTime;

            return after => NextCalendar(calendar, first, zone, after);
        }

        var cron = ParseCron(definition.Cron);

        return after => cron.GetNextOccurrence(after, zone);
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

    /// <summary>The first window start + k × interval, on an included day and no later than the window end, after <paramref name="after"/>.</summary>
    private static DateTimeOffset? NextInWindow(TimeSpan interval, DailyWindow window, TimeZoneInfo zone, DateTimeOffset after)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        for (var day = 0; day < WindowSearchDays; day++)
        {
            var date = local.Date.AddDays(day);
            if (window.Days is { } days && !days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var first = date.Add(window.From.ToTimeSpan());
            var next = day > 0 || local < first ? first : first + TimeSpan.FromTicks(((local - first).Ticks / interval.Ticks + 1) * interval.Ticks);
            if (next <= date.Add(window.Until.ToTimeSpan()) && ToInstant(next, zone) is var instant && instant > after)
            {
                return instant;
            }
        }

        return null;
    }

    /// <summary>The first local time first + k units, as an instant, after <paramref name="after"/>.</summary>
    private static DateTimeOffset NextCalendar(CalendarInterval interval, DateTime first, TimeZoneInfo zone, DateTimeOffset after)
    {
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var estimate = interval.Unit switch
        {
            CalendarUnit.Day => (int)((local - first).TotalDays / interval.Count),
            CalendarUnit.Week => (int)((local - first).TotalDays / (7 * interval.Count)),
            CalendarUnit.Month => ((local.Year - first.Year) * 12 + local.Month - first.Month) / interval.Count,
            _ => (local.Year - first.Year) / interval.Count
        };
        for (var step = Math.Max(0, estimate - 1); ; step++)
        {
            var units = step * interval.Count;
            var next = ToInstant(interval.Unit switch
            {
                CalendarUnit.Day => first.AddDays(units),
                CalendarUnit.Week => first.AddDays(7 * units),
                CalendarUnit.Month => first.AddMonths(units),
                _ => first.AddYears(units)
            }, zone);
            if (next > after)
            {
                return next;
            }
        }
    }

    /// <summary>A local time in <paramref name="zone"/> as an instant; a time skipped by a daylight-saving change moves forward an hour.</summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new(local, zone.GetUtcOffset(local));
    }
}

/// <summary>Lifecycle changes return the new revision.</summary>
/// <summary>A calendar stored under a name that schedules reference; <see cref="Revision"/> guards concurrent edits.</summary>
public sealed record NamedCalendar(string Name, ScheduleCalendar Calendar, int Revision)
{
    public const int MaximumNameLength = 120;

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength || name != name.Trim())
        {
            throw new ArgumentException($"A calendar name needs 1 to {MaximumNameLength} characters without surrounding spaces.", nameof(name));
        }
    }
}

/// <summary>
/// Named calendars, edited at runtime. Saving one moves the next occurrence of every published schedule that names it in the same
/// transaction; a calendar a schedule still names cannot be deleted.
/// </summary>
public interface IScheduleCalendars
{
    Task<IReadOnlyList<NamedCalendar>> CalendarsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the calendar when <paramref name="expectedRevision"/> is 0, otherwise replaces that revision. Returns the new revision.</summary>
    Task<Result<int>> SaveCalendarAsync(string name, ScheduleCalendar calendar, int expectedRevision, AdministrativeCommand command,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteCalendarAsync(string name, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
}

public interface IJobSchedules
{
    Task<Result<int>> SaveScheduleDraftAsync(ScheduleDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> PublishScheduleAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<Result<int>> SetScheduleEnabledAsync(string key, int version, int expectedRevision, bool enabled, AdministrativeCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleOccurrence>> OccurrencesAsync(string key, CancellationToken cancellationToken = default);
    Task<int> DispatchDueSchedulesAsync(int batchSize = 100, CancellationToken cancellationToken = default);
}
