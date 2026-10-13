using Cronos;

namespace WorkThrough;

/// <summary>A month and day that recur every year, such as 25 December.</summary>
public sealed record MonthDay(int Month, int Day);

/// <summary>
/// Times a schedule skips, evaluated in the schedule's time zone: specific dates, dates every year, weekdays, days of the month, a daily
/// time range and cron expressions. Define a calendar once and apply it to many schedules with
/// <see cref="ScheduleDefinition.Excluding(ScheduleCalendar)"/>; each schedule keeps its own copy, so changing a calendar means saving
/// new schedule versions.
/// </summary>
/// <example><c>new ScheduleCalendar().ExcludingWeekdays(DayOfWeek.Saturday, DayOfWeek.Sunday).ExcludingEveryYear(12, 25)</c></example>
public sealed record ScheduleCalendar
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DateOnly>? Dates { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MonthDay>? AnnualDates { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DayOfWeek>? Weekdays { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? DaysOfMonth { get; init; }

    /// <summary>The start of a time range skipped every day; when it is after <see cref="DailyUntil"/>, the range spans midnight.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeOnly? DailyFrom { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeOnly? DailyUntil { get; init; }

    /// <summary>Cron expressions whose matching times are skipped, five fields or six with seconds.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Crons { get; init; }

    public ScheduleCalendar ExcludingDates(params DateOnly[] dates) => this with { Dates = [.. Dates ?? [], .. dates] };

    public ScheduleCalendar ExcludingEveryYear(int month, int day) => this with { AnnualDates = [.. AnnualDates ?? [], new MonthDay(month, day)] };

    public ScheduleCalendar ExcludingWeekdays(params DayOfWeek[] days) => this with { Weekdays = [.. Weekdays ?? [], .. days] };

    public ScheduleCalendar ExcludingDaysOfMonth(params int[] days) => this with { DaysOfMonth = [.. DaysOfMonth ?? [], .. days] };

    /// <summary>Skips <paramref name="from"/> up to, not including, <paramref name="until"/> every day.</summary>
    public ScheduleCalendar ExcludingTimes(TimeOnly from, TimeOnly until) => this with { DailyFrom = from, DailyUntil = until };

    public ScheduleCalendar ExcludingCron(string cron) => this with { Crons = [.. Crons ?? [], cron] };

    /// <summary>A calendar that skips everything either calendar skips. A second daily range replaces the first.</summary>
    public ScheduleCalendar Combine(ScheduleCalendar other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new()
        {
            Dates = Union(Dates, other.Dates),
            AnnualDates = Union(AnnualDates, other.AnnualDates),
            Weekdays = Union(Weekdays, other.Weekdays),
            DaysOfMonth = Union(DaysOfMonth, other.DaysOfMonth),
            DailyFrom = other.DailyFrom ?? DailyFrom,
            DailyUntil = other.DailyFrom is null ? DailyUntil : other.DailyUntil,
            Crons = Union(Crons, other.Crons)
        };
    }

    private static IReadOnlyList<T>? Union<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second) =>
        first is null && second is null ? null : [.. (first ?? []).Concat(second ?? []).Distinct()];

    internal void Validate()
    {
        if (AnnualDates?.Any(date => date.Month is < 1 or > 12 || date.Day < 1 || date.Day > DateTime.DaysInMonth(2024, date.Month)) == true
            || DaysOfMonth?.Any(day => day is < 1 or > 31) == true || DailyFrom is null != DailyUntil is null || DailyFrom == DailyUntil && DailyFrom is not null)
        {
            throw new ArgumentException("A schedule calendar has an invalid yearly date, day of the month or daily range.");
        }

        foreach (var cron in Crons ?? [])
        {
            _ = ScheduleClock.ParseCron(cron);
        }
    }

    /// <summary>When <paramref name="instant"/> is skipped, the local time from which it may not be; otherwise null.</summary>
    internal DateTime? SkippedUntil(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var nextDay = local.Date.AddDays(1);
        if (Dates?.Contains(DateOnly.FromDateTime(local)) == true || AnnualDates?.Contains(new MonthDay(local.Month, local.Day)) == true
            || Weekdays?.Contains(local.DayOfWeek) == true || DaysOfMonth?.Contains(local.Day) == true)
        {
            return nextDay;
        }

        if (DailyFrom is { } from && DailyUntil is { } until)
        {
            var time = TimeOnly.FromDateTime(local);
            var spansMidnight = from > until;
            if (spansMidnight ? time >= from || time < until : time >= from && time < until)
            {
                return (spansMidnight && time >= from ? nextDay : local.Date).Add(until.ToTimeSpan());
            }
        }

        foreach (var expression in Crons ?? [])
        {
            var seconds = ScheduleClock.HasSeconds(expression);
            var step = seconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
            var start = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % step.Ticks, TimeSpan.Zero);
            if (ScheduleClock.ParseCron(expression).GetNextOccurrence(start, zone, inclusive: true) == start)
            {
                return local - TimeSpan.FromTicks(local.Ticks % step.Ticks) + step;
            }
        }

        return null;
    }
}

/// <summary>Repeats a schedule's interval only inside a daily time range, optionally on certain weekdays, counted from the range start each day.</summary>
public sealed record DailyWindow(TimeOnly From, TimeOnly Until, IReadOnlyList<DayOfWeek>? Days = null);

public enum CalendarUnit
{
    Day, Week, Month, Year
}

/// <summary>
/// Repeats every <paramref name="Count"/> days, weeks, months or years at the same local time as the schedule's start, in its time zone,
/// so daylight-saving changes do not shift it. A month step from the 31st lands on each month's last day.
/// </summary>
public sealed record CalendarInterval(int Count, CalendarUnit Unit);
