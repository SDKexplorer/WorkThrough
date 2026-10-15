# Durable schedules

Use `JobClient.EnqueueAsync(processorId, input, new(NotBefore: instant))` for a one-time scheduled invocation. The time is an eligibility instant, not a promised start time under load.

For recurrence, add `.AddScheduler()` to any host using the store; it does not require `.AddWorker()`. Multiple schedulers may run against the same store. They use the provider write lock and database clock to commit the occurrence record, job, and next due instant in one transaction. The unique occurrence identity is schedule key, version, and UTC due instant. Processor identity stays independent of schedule identity and CLR location.

`IJobSchedules` supports editable drafts with revision checks, validated publication, and independently audited enable/pause commands. Only one version of a schedule key can be enabled at a time; old deployments cannot silently overwrite a published definition. Pause the active version before enabling its replacement. Jobs already created continue when a schedule is paused.

A schedule fires on one of three timings, in an explicit installed time zone ([ADR-030](adr/0030-richer-schedules-and-quartz-import.md)):

- **Cron:** five fields, or six with seconds first.
- **Interval:** a fixed period counted from the schedule's start, or from the Unix epoch; `Within(from, until, days)` limits it to daily hours on chosen weekdays.
- **Calendar interval:** every so many days, weeks, months or years at the same local time, across daylight-saving changes.

`Starting` and `Ending` bound a schedule. A `ScheduleCalendar` skips dates, dates every year, weekdays, days of the month, a daily time range and cron matches; define one calendar and apply it to many schedules with `Excluding(calendar)`:

```csharp
var office = new ScheduleCalendar()
    .ExcludingEveryYear(12, 25)
    .ExcludingDates(new DateOnly(2026, 12, 26));

builder.Services.AddWorkThrough()
    .Job(Reports.Daily, job => job
        .RunWith<DailyReportJob>()
        .Every("0 0 9 * * MON-FRI", new DailyReport(1), "Europe/Bucharest", schedule: s => s.Excluding(office)))
    .Job(Health.Probe, job => job
        .RunWith<ProbeJob>()
        .Every(TimeSpan.FromMinutes(10), new Probe("api"), schedule: s => s.Within(new(9, 0), new(17, 0), DayOfWeek.Monday, DayOfWeek.Friday)))
    .Job(Billing.Invoice, job => job
        .RunWith<InvoiceJob>()
        .EveryCalendar(1, CalendarUnit.Month, new DateTimeOffset(2026, 1, 31, 7, 0, 0, TimeSpan.Zero), new Invoice(), "Europe/Bucharest"));
```

### Named calendars

A calendar copied into a schedule with `Excluding` is fixed with that schedule version. For holidays that operators maintain, store a named calendar and let schedules name it:

```csharp
await calendars.SaveCalendarAsync("holidays", new ScheduleCalendar().ExcludingEveryYear(12, 25), expectedRevision: 0, command);

ScheduleDefinition.Create("reports.daily", "0 9 * * *", Reports.Daily, new DailyReport(1)).ExcludingCalendar("holidays");
```

`IScheduleCalendars` saves and deletes calendars as audited commands, guarded by a revision; the console edits them under Schedules with `schedules.manage`. Saving a calendar moves the next occurrence of every published schedule that names it, in the same transaction. An occurrence already due is checked again, so a newly excluded date stops it, and a removed exclusion brings an earlier occurrence back. A schedule's own calendar and every calendar it names combine. Publishing or enabling a schedule fails while a calendar it names is missing. A calendar cannot be deleted while a draft or an enabled schedule names it ([ADR-032](adr/0032-named-calendars.md)).

Schedules that use seconds, intervals, calendar intervals, windows, bounds or calendars need engine protocol 9, and those naming calendars need protocol 10; schedulers from earlier releases would fire them at the wrong times. [Cronos](https://github.com/HangfireIO/Cronos) supplies calendar/DST calculations; daily non-interval triggers are not repeated during fall-back, while its documented interval behavior is retained. Use the same time-zone database across scheduler hosts. UTC is simplest when wall-clock business timing is unnecessary.

Overlap policies:

- `Allow`: create every selected occurrence even if previous jobs are queued/running.
- `SkipIfActive`: retain a skipped occurrence when prior work for the same schedule key is queued/running.
- `Coalesce`: retain one pending due occurrence until prior work ends, then emit once and calculate the next future time. Fair batch scanning keeps blocked schedules from permanently hiding other due schedules.

An occurrence over 30 seconds late is a misfire. `Skip` records it as skipped and advances; `Coalesce` emits one and advances past the missed interval; `CatchUp` walks forward one due occurrence per schedule per dispatch batch. Coalesced overlap takes precedence over catch-up. Dispatch batches are bounded to 100 schedules by the hosted scheduler; the storage API permits 1–500. Occurrence history identifies emitted/skipped work, not a manufactured successful handler attempt.

For schedules that belong to the application rather than to operators, seed them at startup:

```csharp
builder.Services.AddWorkThrough()
    .SeedSchedules(ScheduleDefinition.Create("reports.daily", "0 6 * * *", Reports.Daily, new DailyReport("global"), "Europe/Bucharest"))
    .AddScheduler();
```

A seeded schedule that does not exist yet is drafted, published and enabled through the same audited commands, with command IDs derived from its key and version, so hosts starting together create it once. Existing versions, including ones operators paused or edited, are never changed. To replace a seeded schedule, seed a new version: it is published but stays paused while the old version is enabled, and an operator switches them in the console. The [Shop sample](../samples/README.md) seeds a daily report this way. Retrying a job does not create another occurrence. Schedule mutation does not undo prior effects. 

## Scheduled flows

A schedule can start a flow instead of a single job. Every occurrence starts one run; a retried attempt finds the run it already started:

```csharp
var nightly = new FlowBuilder("reports.nightly", 1)
    .Step("collect", Reports.Collect)
    .Step("send", Reports.Send).After("collect")
    .Build();

builder.Services.AddWorkThrough()
    .SeedSchedules(ScheduledFlows.Create("reports.nightly", "0 2 * * *", nightly, new { }));
```

The schedule targets the built-in `workthrough.flows.start` job, which every host registers. A flow can also start later without a schedule: `new FlowStart { NotBefore = tomorrowAtNine }`.

## Schedule options

`ScheduleDefinition` carries the processor address and fixed input, plus `Queue`, `MaxAttempts`, `Overlap`, `Misfire`, `CorrelationId`, `PartitionKey` and `TenantId` for the jobs it creates. Admission limits apply to those jobs like any other.

## Not supported

- **Fixed intervals and daylight saving:** a 1-day `Every(TimeSpan)` shifts by an hour across a change; use `EveryCalendar` or cron for "every day at 09:00".
- **Recurring flow starts:** a schedule creates a job. To start a flow on a schedule, schedule a job that starts it with a flow idempotency key derived from its occurrence.
- **Encryption:** schedule inputs are stored as plain JSON even with payload protection ([ADR-027](adr/0027-payload-protection.md)).

Hangfire recurring jobs import as schedules, including their time zone and seconds ([migrating from Hangfire](migrating-from-hangfire.md)). Quartz.NET triggers import as cron or interval schedules with their bounds and holiday calendars ([migrating from Quartz.NET](migrating-from-quartz.md)).

## Testing schedules

In `WorkThroughTestHost`, `AdvanceAsync` moves the store clock, so a test can seed a schedule, advance past its due time and wait for the job ([testing](testing.md)).
