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
| Holiday calendar | `schedule: s => s.Excluding(holidays)` |
| `[DisallowConcurrentExecution]` | `ScheduleOverlap.Coalesce`, or `.AtMostRunning(1)` |
| Misfire instructions | `ScheduleMisfire.Skip`, `Coalesce` or `CatchUp` |
| `scheduler.TriggerJob(key)` | `await jobs.Enqueue(definition, input)` |
| Job listeners and chaining | Flows with steps that run `.After(...)` each other |
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
- unmapped job classes;
- failed conversions;
- year-restricted crons;
- daily-time-interval triggers;
- non-holiday calendars;
- day or week intervals in zones with daylight saving.

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

- Calendars other than plain holiday calendars do not import; express them with cron fields or excluded dates.
- Quartz's job listeners, trigger listeners and priorities have no import; rebuild chains as flows.
- `WorkThrough.Migration.Quartz` is built against Quartz.NET 3.22. Quartz 4 is not yet verified.
