using System.Text.Json;
using System.Text.RegularExpressions;
using Quartz;
using Quartz.Impl.Calendar;
using Quartz.Impl.Matchers;

namespace WorkThrough.Migration.Quartz;

/// <summary>
/// Moves Quartz.NET jobs into WorkThrough. Map each Quartz job class to a WorkThrough address, review the <see cref="PlanAsync"/>, put the
/// scheduler in standby on every node, <see cref="ImportAsync"/>, then <see cref="RetireAsync"/> the imported triggers. Cron, simple and
/// calendar-interval triggers become schedules, a trigger that fires once more becomes a delayed job, and holiday calendars become
/// excluded dates. Reads go through the scheduler's API, so any Quartz job store works.
/// </summary>
public sealed partial class QuartzMigration(IScheduler scheduler)
{
    private const string IdempotencyPrefix = "quartz:";
    private const string SchedulePrefix = "quartz.";
    private const int MaximumKeyLength = 120;
    private const int RepeatIndefinitely = -1;
    private static readonly OperationActor Actor = new("workthrough", "quartz-migration", "Quartz.NET migration");

    private readonly List<Mapping> mappings = [];
    private Func<TriggerKey, string> scheduleKey = DefaultScheduleKey;

    private sealed record Mapping(Type JobType, JobContractId Target, Func<QuartzJobData, JsonElement> Convert, string? Queue);

    /// <summary>Imports triggers of jobs run by <typeparamref name="TJob"/> as work at <paramref name="target"/>, with the input <paramref name="convert"/> builds.</summary>
    /// <example><c>.Map&lt;SendReminderJob, Reminder&gt;(Reminders.Send, data =&gt; new Reminder(data.GetString("customer")!))</c></example>
    public QuartzMigration Map<TJob, TInput>(JobDefinition<TInput> target, Func<QuartzJobData, TInput> convert, string? queue = null)
        where TJob : global::Quartz.IJob
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(convert);
        mappings.Add(new(typeof(TJob), target.Identity, data => JsonSerializer.SerializeToElement(convert(data), JobJson.Options), queue ?? target.Queue));

        return this;
    }

    /// <summary>Names the WorkThrough schedule for each trigger; by default <c>quartz.</c> plus its group and name in lowercase.</summary>
    public QuartzMigration NameSchedules(Func<TriggerKey, string> key)
    {
        scheduleKey = key ?? throw new ArgumentNullException(nameof(key));

        return this;
    }

    /// <summary>Reads every job and trigger and decides what each trigger becomes. Changes nothing.</summary>
    public async Task<QuartzMigrationPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<QuartzMigrationItem>();
        foreach (var jobKey in (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken)).OrderBy(key => key.ToString(), StringComparer.Ordinal))
        {
            var job = await scheduler.GetJobDetail(jobKey, cancellationToken);
            if (job is null)
            {
                continue;
            }

            foreach (var trigger in (await scheduler.GetTriggersOfJob(jobKey, cancellationToken)).OrderBy(t => t.Key.ToString(), StringComparer.Ordinal))
            {
                items.Add(await PlanTriggerAsync(job, trigger, cancellationToken));
            }
        }

        var running = scheduler.IsStarted && !scheduler.InStandbyMode && !scheduler.IsShutdown;

        return new(DuplicateSchedules(items), running, (await scheduler.GetCurrentlyExecutingJobs(cancellationToken)).Count);
    }

    private async Task<QuartzMigrationItem> PlanTriggerAsync(IJobDetail job, ITrigger trigger, CancellationToken ct)
    {
        var item = new QuartzMigrationItem(job.Key.ToString(), trigger.Key.Group, trigger.Key.Name, job.JobType.FullName ?? job.JobType.Name);
        var mapping = mappings.FirstOrDefault(candidate => candidate.JobType == job.JobType);
        if (mapping is null)
        {
            return item with { Problem = "No mapping for this job class." };
        }

        JsonElement input;
        try
        {
            input = mapping.Convert(new(item.JobKey, item.TriggerKey, Merge(job.JobDataMap, trigger.JobDataMap)));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidCastException or FormatException or JsonException)
        {
            return item with { Problem = $"The mapping could not convert the job data: {error.Message}" };
        }

        item = item with { Target = mapping.Target, Input = input, Queue = mapping.Queue ?? JobDefaults.Queue };
        if (trigger is ISimpleTrigger { RepeatCount: 0 })
        {
            return trigger.GetNextFireTimeUtc() is { } once
                ? item with { Kind = QuartzImportKind.OneTime, NotBefore = once }
                : item with { Problem = "The trigger has already fired and will not fire again." };
        }

        var (schedule, problem) = ScheduleOf(trigger);
        if (schedule is null)
        {
            return item with { Problem = problem };
        }

        if (trigger.CalendarName is { } calendarName)
        {
            var calendar = await scheduler.GetCalendar(calendarName, ct);
            if (calendar is not HolidayCalendar { CalendarBase: null } holidays)
            {
                return item with { Problem = $"Calendar {calendarName} is not a plain holiday calendar; only excluded dates import." };
            }

            schedule = schedule.Excluding([.. holidays.ExcludedDates.Select(DateOnly.FromDateTime)]);
        }

        schedule = schedule with
        {
            Key = scheduleKey(trigger.Key),
            Processor = mapping.Target,
            Input = input,
            Queue = item.Queue,
            Overlap = job.ConcurrentExecutionDisallowed ? ScheduleOverlap.Coalesce : ScheduleOverlap.Allow,
            Misfire = Misfire(trigger),
            StartsAt = schedule.StartsAt ?? (trigger.StartTimeUtc > DateTimeOffset.UtcNow ? trigger.StartTimeUtc : null),
            EndsAt = Earliest(schedule.EndsAt, trigger.EndTimeUtc)
        };
        try
        {
            ScheduleClock.Validate(schedule);
        }
        catch (ArgumentException error)
        {
            return item with { Problem = $"Not a valid WorkThrough schedule: {error.Message}" };
        }

        var paused = await scheduler.GetTriggerState(trigger.Key, ct) == TriggerState.Paused;

        return item with { Kind = QuartzImportKind.Schedule, Schedule = schedule, Enabled = !paused };
    }

    /// <summary>The schedule a repeating trigger becomes, without its key, target or input; or why it cannot.</summary>
    private static (ScheduleDefinition? Schedule, string? Problem) ScheduleOf(ITrigger trigger)
    {
        var placeholder = new ScheduleDefinition("placeholder", 1, "", ScheduleDefinition.DefaultTimeZone, new("placeholder", 1), JsonSerializer.SerializeToElement(new { }));
        switch (trigger)
        {
            case ICronTrigger cron:
                var (expression, problem) = CronOf(cron.CronExpressionString ?? "");

                return expression is null ? (null, problem) : (placeholder with { Cron = expression, TimeZone = cron.TimeZone.Id }, null);
            case ISimpleTrigger simple:
                var start = simple.StartTimeUtc;
                var end = simple.RepeatCount == RepeatIndefinitely ? (DateTimeOffset?)null : start + simple.RepeatInterval * simple.RepeatCount;

                return (placeholder with { Interval = simple.RepeatInterval, StartsAt = start, EndsAt = end }, null);
            case ICalendarIntervalTrigger calendar:
                var unit = calendar.RepeatIntervalUnit switch
                {
                    IntervalUnit.Second => TimeSpan.FromSeconds(1),
                    IntervalUnit.Minute => TimeSpan.FromMinutes(1),
                    IntervalUnit.Hour => TimeSpan.FromHours(1),
                    IntervalUnit.Day or IntervalUnit.Week when calendar.TimeZone.SupportsDaylightSavingTime => (TimeSpan?)null,
                    IntervalUnit.Day => TimeSpan.FromDays(1),
                    IntervalUnit.Week => TimeSpan.FromDays(7),
                    _ => null
                };

                return unit is null
                    ? (null, $"A calendar interval of {calendar.RepeatInterval} {calendar.RepeatIntervalUnit} in {calendar.TimeZone.Id} keeps wall-clock time; use a cron schedule instead.")
                    : (placeholder with { Interval = unit.Value * calendar.RepeatInterval, StartsAt = calendar.StartTimeUtc }, null);
            default:
                return (null, $"{trigger.GetType().Name} triggers do not import; recreate them as cron or interval schedules.");
        }
    }

    /// <summary>
    /// A Quartz cron expression as a six-field Cronos expression. Quartz numbers weekdays 1–7 from Sunday, Cronos 0–6, and Quartz allows
    /// a seventh year field, which only imports when it is unrestricted.
    /// </summary>
    public static (string? Cron, string? Problem) CronOf(string quartz)
    {
        var fields = quartz.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is < 6 or > 7)
        {
            return (null, $"'{quartz}' is not a Quartz cron expression.");
        }

        if (fields.Length == 7 && fields[6] is not ("*" or "?"))
        {
            return (null, $"'{quartz}' is limited to particular years, which WorkThrough schedules cannot express.");
        }

        if (fields[5] == "L")
        {
            return (null, $"'{quartz}' uses L alone in the day-of-week field.");
        }

        fields[5] = QuartzWeekday().Replace(fields[5], day => (int.Parse(day.Value, System.Globalization.CultureInfo.InvariantCulture) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        return (string.Join(' ', fields.Take(6)), null);
    }

    /// <summary>A weekday number, not a step after '/' or an occurrence after '#'.</summary>
    [GeneratedRegex("(?<![/#0-9])[1-7](?![0-9])")]
    private static partial Regex QuartzWeekday();

    private static ScheduleMisfire Misfire(ITrigger trigger) => trigger.MisfireInstruction switch
    {
        MisfireInstruction.IgnoreMisfirePolicy => ScheduleMisfire.CatchUp,
        MisfireInstruction.CronTrigger.DoNothing when trigger is ICronTrigger => ScheduleMisfire.Skip,
        MisfireInstruction.SimpleTrigger.RescheduleNextWithExistingCount or MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount
            when trigger is ISimpleTrigger => ScheduleMisfire.Skip,
        _ => ScheduleMisfire.Coalesce
    };

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is { } a && second is { } b ? (a < b ? a : b) : first ?? second;

    private static IReadOnlyDictionary<string, object?> Merge(JobDataMap job, JobDataMap trigger)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in job.Concat(trigger))
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private static List<QuartzMigrationItem> DuplicateSchedules(List<QuartzMigrationItem> items)
    {
        var duplicates = items.Where(item => item.Schedule is not null).GroupBy(item => item.Schedule!.Key).Where(group => group.Count() > 1)
            .SelectMany(group => group).ToHashSet();

        return [.. items.Select(item => duplicates.Contains(item)
            ? item with { Problem = $"Schedule key {item.Schedule!.Key} is shared with another trigger; use NameSchedules." }
            : item)];
    }

    private static string DefaultScheduleKey(TriggerKey trigger)
    {
        var key = SchedulePrefix + InvalidKeyCharacters().Replace($"{trigger.Group}.{trigger.Name}".ToLowerInvariant(), "-");

        return key.Length <= MaximumKeyLength ? key : key[..MaximumKeyLength];
    }

    [GeneratedRegex("[^a-z0-9.-]")]
    private static partial Regex InvalidKeyCharacters();

    /// <summary>
    /// Imports every ready item: repeating triggers become published schedules, enabled unless paused in Quartz, and one-time triggers
    /// become delayed jobs under the idempotency key <c>quartz:&lt;trigger&gt;</c>. Running it again imports nothing twice. Refuses while
    /// this scheduler runs, unless <paramref name="allowRunningScheduler"/>; put every clustered node in standby too.
    /// </summary>
    public async Task<QuartzMigrationResult> ImportAsync(QuartzMigrationPlan plan, IJobStore store, IJobSchedules schedules,
        bool allowRunningScheduler = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schedules);
        if (plan.SchedulerRunning && !allowRunningScheduler)
        {
            throw new InvalidOperationException("The Quartz scheduler is running and could fire the same triggers. Put it in standby, then plan again.");
        }

        var client = new JobClient(store);
        var existing = (await schedules.SchedulesAsync(cancellationToken)).Select(schedule => schedule.Definition.Key).ToHashSet(StringComparer.Ordinal);
        var imported = new List<QuartzImported>();
        var skipped = plan.Problems.ToList();
        foreach (var item in plan.Ready)
        {
            if (item.Kind == QuartzImportKind.OneTime)
            {
                var id = await client.EnqueueAsync(new JobDefinition<JsonElement>(item.Target!), item.Input!.Value,
                    new(Queue: item.Queue, NotBefore: item.NotBefore, IdempotencyKey: IdempotencyPrefix + item.TriggerKey), cancellationToken);
                imported.Add(new(item, id));

                continue;
            }

            var created = existing.Contains(item.Schedule!.Key) ? null : await schedules.CreatePublishedAsync(item.Schedule, item.Enabled, Actor,
                IdempotencyPrefix + item.TriggerKey, $"Imported from Quartz trigger {item.TriggerKey}", cancellationToken);
            if (created is null || created.IsSuccess)
            {
                imported.Add(new(item, null));
            }
            else
            {
                skipped.Add(item with { Problem = created.Error!.Message });
            }
        }

        return new(imported, skipped);
    }

    /// <summary>
    /// Unschedules the imported triggers in Quartz, so restarting it cannot fire them again; a job without other triggers goes with its
    /// last one unless it is durable. Call it after checking the import; skipped triggers stay.
    /// </summary>
    public async Task RetireAsync(QuartzMigrationResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var item in result.Imported.Select(imported => imported.Item))
        {
            await scheduler.UnscheduleJob(new TriggerKey(item.TriggerName, item.TriggerGroup), cancellationToken);
        }
    }
}
