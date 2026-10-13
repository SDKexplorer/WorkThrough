# Durable schedules

Use `JobClient.EnqueueAsync(processorId, input, new(NotBefore: instant))` for a one-time scheduled invocation. The time is an eligibility instant, not a promised start time under load.

For recurrence, add `.AddScheduler()` to any host using the store; it does not require `.AddWorker()`. Multiple schedulers may run against the same store. They use the provider write lock and database clock to commit the occurrence record, job, and next due instant in one transaction. The unique occurrence identity is schedule key, version, and UTC due instant. Processor identity stays independent of schedule identity and CLR location.

`IJobSchedules` supports editable drafts with revision checks, validated publication, and independently audited enable/pause commands. Only one version of a schedule key can be enabled at a time; old deployments cannot silently overwrite a published definition. Pause the active version before enabling its replacement. Jobs already created continue when a schedule is paused.

A schedule fires on a cron expression or a fixed interval, in an explicit installed time zone. Cron has five fields, or six with seconds first; an interval counts from the schedule's start, or from the Unix epoch. `Starting`, `Ending` and `Excluding(dates)` bound a schedule and skip dates such as holidays ([ADR-030](adr/0030-richer-schedules-and-quartz-import.md)):

```csharp
.Job(Reports.Daily, job => job
    .RunWith<DailyReportJob>()
    .Every("0 0 9 * * MON-FRI", new DailyReport(1), "Europe/Bucharest", schedule: s => s.Excluding(Holidays.Romania2026)))
.Job(Health.Probe, job => job
    .RunWith<ProbeJob>()
    .Every(TimeSpan.FromSeconds(30), new Probe("api")))
```

Schedules with seconds, intervals, bounds or excluded dates need engine protocol 9; schedulers from earlier releases would fire them at the wrong times. [Cronos](https://github.com/HangfireIO/Cronos) supplies calendar/DST calculations; daily non-interval triggers are not repeated during fall-back, while its documented interval behavior is retained. Use the same time-zone database across scheduler hosts. UTC is simplest when wall-clock business timing is unnecessary.

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

## Schedule options

`ScheduleDefinition` carries the processor address and fixed input, plus `Queue`, `MaxAttempts`, `Overlap`, `Misfire`, `CorrelationId`, `PartitionKey` and `TenantId` for the jobs it creates. Admission limits apply to those jobs like any other.

## Not supported

- **Shared calendars:** excluded dates belong to each schedule; there is no calendar shared by name. Repeat the dates, or keep them in one constant.
- **Wall-clock intervals:** an interval is a fixed period, so a 1-day interval shifts by an hour across a daylight-saving change. Use a cron expression for "every day at 09:00".
- **Daily time windows:** there is no "every 10 minutes between 09:00 and 17:00" trigger; use cron (`0 */10 9-16 * * *`).
- **Recurring flow starts:** a schedule creates a job. To start a flow on a schedule, schedule a job that starts it with a flow idempotency key derived from its occurrence.
- **Encryption:** schedule inputs are stored as plain JSON even with payload protection ([ADR-027](adr/0027-payload-protection.md)).

Hangfire recurring jobs import as schedules, including their time zone and seconds ([migrating from Hangfire](migrating-from-hangfire.md)). Quartz.NET triggers import as cron or interval schedules with their bounds and holiday calendars ([migrating from Quartz.NET](migrating-from-quartz.md)).

## Testing schedules

In `WorkThroughTestHost`, `AdvanceAsync` moves the store clock, so a test can seed a schedule, advance past its due time and wait for the job ([testing](testing.md)).
