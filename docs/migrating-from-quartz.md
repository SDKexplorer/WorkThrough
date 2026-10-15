# Migrating from Quartz.NET

An importer moves Quartz.NET jobs and their triggers into WorkThrough schedules in one step ([ADR-030](adr/0030-richer-schedules-and-quartz-import.md)). New code can use WorkThrough while Quartz keeps running the rest until the cutover.

## Concepts

| Quartz.NET | WorkThrough |
|---|---|
| `IJob` class with `JobDataMap` | `IJob<TInput>` with a typed input record, or a delegate |
| `JobKey` | A `JobDefinition` address such as `reports.cleanup` v1 |
| Cron trigger | `.Every("0 0 2 * * ?", input)`; six fields start with seconds |
| Simple trigger | `.Every(TimeSpan.FromMinutes(30), input)` |
| `StartAt` / `EndAt` | `schedule: s => s.Starting(...).Ending(...)` |
| Calendars (holiday, annual, weekly, monthly, daily, cron) | `ScheduleCalendar` and `schedule: s => s.Excluding(calendar)` |
| Daily-time-interval trigger | `.Every(interval, input, schedule: s => s.Within(from, until, days))` |
| Calendar-interval trigger | `.EveryCalendar(count, unit, first, input, timeZone)` |
| `[DisallowConcurrentExecution]` | `ScheduleOverlap.Coalesce`, or `.AtMostRunning(1)` |
| Misfire instructions | `ScheduleMisfire.Skip`, `Coalesce` or `CatchUp` |
| `scheduler.TriggerJob(key)` | `await jobs.Enqueue(definition, input)` |
| `JobChainingJobListener` chains | A flow: each chained job is a step that runs after the previous one succeeds; the trigger becomes a schedule that starts it |
| Clustered ADO.NET job store | Any WorkThrough provider; every scheduler host shares the store |

Quartz stores the job's CLR type name, so moving the class breaks stored jobs. WorkThrough stores the address and the input.

## 1. Write the WorkThrough jobs

```csharp
public sealed record Cleanup(int Days);

public static class Maintenance
{
    public static JobDefinition<Cleanup> Cleanup { get; } = new("maintenance.cleanup");
}

builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .Job(Maintenance.Cleanup, job => job.RunWith<CleanupJob>())
    .AddScheduler()
    .AddWorker();
```

Do not seed the schedules the importer will create; it creates them with the Quartz trigger's timing.

## 2. Plan the import

In a host where the Quartz job classes still load, get the scheduler for the existing job store without starting it, and map each job class:

```csharp
var scheduler = await new StdSchedulerFactory(quartzProperties).GetScheduler();  // not started: standby
var migration = new QuartzMigration(scheduler)
    .Map<NightlyCleanupJob, Cleanup>(Maintenance.Cleanup, data => new Cleanup(data.Get<int>("days")));

var plan = await migration.PlanAsync();
foreach (var problem in plan.Problems)
{
    Console.WriteLine($"{problem.TriggerKey} ({problem.JobType}): {problem.Problem}");
}
```

The converter receives the trigger's job data, merged with the job's as Quartz does. `Get<T>` converts the strings Quartz stores. The plan converts each trigger as the [ADR](adr/0030-richer-schedules-and-quartz-import.md#decision) describes, and reports:
- unmapped job classes, and job classes that no longer load;
- durable jobs without triggers;
- failed conversions;
- year-restricted crons;
- daily-time-interval triggers with a repeat count;
- custom calendars, and calendars in another time zone than their trigger. A calendar without an explicit time zone uses the local zone of the machine running the import, as Quartz uses its host's; run the import where Quartz ran, or set the zone.

## 3. Stop Quartz and import

Put every Quartz node in standby or shut it down; `ImportAsync` refuses while the scheduler it reads from runs, but cannot see other clustered nodes. Then:

```csharp
var result = await migration.ImportAsync(await migration.PlanAsync(), store, store);
```

- Repeating triggers become published schedules named `quartz.<group>.<name>`, paused if the trigger was paused. `NameSchedules(key => ...)` picks other names.
- A trigger that fires once more becomes a delayed job with the idempotency key `quartz:<group>.<name>`.
- Running the import again creates nothing twice.
- Schedules with seconds, intervals, bounds or excluded dates need engine protocol 9: upgrade every worker and scheduler first.

## 4. Retire the triggers in Quartz

Check the schedules in the WorkThrough console, then:

```csharp
await migration.RetireAsync(result);
```

This unschedules the imported triggers, so restarting Quartz cannot fire them again. Skipped triggers stay.

## Limits

- Quartz reads a job's class to load it, so the importer must run where the old job classes load.
- Job chains move: a trigger of a job with jobs chained after it becomes a schedule that starts a flow of the whole chain (a one-time trigger, a flow that starts at its time). The importer reads the links of a `JobChainingJobListener` registered on the scheduler it is given; a migration tool usually builds its own scheduler, so declare the links with `Chain(firstJobKey, thenJobKey)`. Every job of a chain needs a mapping, or the trigger stays in Quartz. A chained job receives its own job data, as in Quartz.
- Other job listeners, trigger listeners and priorities have no import.
- `WorkThrough.Migration.Quartz` is built against Quartz.NET 3.22. Quartz 4 is not yet verified.
