# ADR 030: richer schedules and Quartz.NET import

Status: accepted, implemented in engine protocol 9.

## Context

Schedules were five-field cron expressions in a time zone. Quartz.NET users also rely on second-level cron, fixed intervals, start and end times and holiday calendars, so their triggers could not move to WorkThrough without changing what they do.

## Decision

`ScheduleDefinition` gains optional properties, stored in its saved JSON, so no table changes:

- **Seconds:** a six-field cron expression starts with seconds. Five fields keep their meaning.
- **Intervals:** `Interval` fires every period, counted from `StartsAt`, or from the Unix epoch so every host agrees on the instants. The cron expression is then empty. The shortest interval is one second, the scheduler's dispatch period.
- **Bounds:** `StartsAt` and `EndsAt` limit the occurrences. A schedule past its end has no next occurrence.
- **Calendars:** `Calendar` skips times, evaluated in the schedule's time zone: dates, dates every year, weekdays, days of the month, a daily time range (which may span midnight) and cron expressions whose matching times are skipped. `Combine` unions calendars, as Quartz chains base calendars.
- **Daily windows:** `Window` limits an interval schedule to a daily time range, optionally on certain weekdays, counted from the range start each day.
- **Calendar intervals:** `CalendarInterval` repeats every so many days, weeks, months or years at the local time of `StartsAt`, so daylight-saving changes do not shift it. A month step from the 31st lands on each month's last day.

Exactly one of a cron expression, an interval or a calendar interval drives a schedule. A scheduler from an earlier release would ignore these properties and fire at the wrong times, so a schedule that uses any of them needs engine protocol 9 (ADR-021) to be saved or published. Dispatch now refuses a store above its build's protocol, as claims do.

`WorkThrough.Migration.Quartz` imports through the scheduler's API, so it works with any Quartz job store:

| Quartz | WorkThrough |
|---|---|
| Cron trigger | Six-field cron in the trigger's time zone; weekday numbers 1–7 from Sunday become 0–6; a seventh year field imports only when unrestricted |
| Simple trigger, repeating | Interval schedule from its start time, ending after its repeat count |
| Simple trigger, firing once more | A delayed job with the idempotency key `quartz:<trigger>` |
| Calendar-interval trigger | Interval schedule for seconds, minutes and hours; calendar interval, keeping local time, for days, weeks, months and years |
| Daily-time-interval trigger | Interval schedule inside a daily window on its weekdays; a finite repeat count is reported |
| Holiday, annual, weekly, monthly, daily and cron calendars, chained | One schedule calendar with all their exclusions; a calendar in another time zone than its trigger is reported |
| `[DisallowConcurrentExecution]` | Overlap `Coalesce`, otherwise `Allow` |
| Misfire: ignore / do nothing / other | `CatchUp` / `Skip` / `Coalesce` |
| Paused trigger | Published, paused schedule |
| Job data | Merged job and trigger data, passed to the mapping's converter |

Import refuses while the scheduler runs, is idempotent, and `RetireAsync` unschedules the imported triggers. A job whose class no longer loads, and a durable job with no triggers, are reported.

The Hangfire importer now keeps six-field crons instead of refusing them; a seconds field of 0 is still dropped, so such schedules need no newer protocol. `MapByName` maps by the stored type and method names and converts the stored JSON arguments, so jobs import even after their classes are gone, and `IncludeFailed` imports failed jobs to run again.

The draft, publish and enable sequence with deterministic command IDs moved into `ScheduleLifecycle.CreatePublishedAsync`, used by startup seeding and both importers.

## Consequences

- Fixed intervals do not keep wall-clock time across daylight-saving changes; calendar intervals do. Use the one that matches the intent.
- Calendars belong to each schedule. Quartz shares a calendar between triggers and can change it at run time; here a calendar defined once in code is copied into every schedule that uses it, and changing it means saving new schedule versions.
- Second-level schedules dispatch within about a second of their due time, with the scheduler's one-second period.
