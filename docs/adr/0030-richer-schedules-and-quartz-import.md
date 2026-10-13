# ADR 030: richer schedules and Quartz.NET import

Status: accepted, implemented in engine protocol 9.

## Context

Schedules were five-field cron expressions in a time zone. Quartz.NET users also rely on second-level cron, fixed intervals, start and end times and holiday calendars, so their triggers could not move to WorkThrough without changing what they do.

## Decision

`ScheduleDefinition` gains optional properties, stored in its saved JSON, so no table changes:

- **Seconds:** a six-field cron expression starts with seconds. Five fields keep their meaning.
- **Intervals:** `Interval` fires every period, counted from `StartsAt`, or from the Unix epoch so every host agrees on the instants. The cron expression is then empty. The shortest interval is one second, the scheduler's dispatch period.
- **Bounds:** `StartsAt` and `EndsAt` limit the occurrences. A schedule past its end has no next occurrence.
- **Excluded dates:** `ExcludedDates` skips occurrences whose date, in the schedule's time zone, is listed: a holiday calendar.

A scheduler from an earlier release would ignore these properties and fire at the wrong times, so a schedule that uses any of them needs engine protocol 9 (ADR-021) to be saved or published. Dispatch now refuses a store above its build's protocol, as claims do.

`WorkThrough.Migration.Quartz` imports through the scheduler's API, so it works with any Quartz job store:

| Quartz | WorkThrough |
|---|---|
| Cron trigger | Six-field cron in the trigger's time zone; weekday numbers 1–7 from Sunday become 0–6; a seventh year field imports only when unrestricted |
| Simple trigger, repeating | Interval schedule from its start time, ending after its repeat count |
| Simple trigger, firing once more | A delayed job with the idempotency key `quartz:<trigger>` |
| Calendar-interval trigger | Interval schedule for seconds, minutes and hours, and for days and weeks in zones without daylight saving; otherwise reported |
| Holiday calendar | Excluded dates; other calendars are reported |
| `[DisallowConcurrentExecution]` | Overlap `Coalesce`, otherwise `Allow` |
| Misfire: ignore / do nothing / other | `CatchUp` / `Skip` / `Coalesce` |
| Paused trigger | Published, paused schedule |
| Job data | Merged job and trigger data, passed to the mapping's converter |

Import refuses while the scheduler runs, is idempotent, and `RetireAsync` unschedules the imported triggers. Daily-time-interval triggers and other calendars are reported, not converted.

The Hangfire importer now keeps six-field crons instead of refusing them; a seconds field of 0 is still dropped, so such schedules need no newer protocol.

The draft, publish and enable sequence with deterministic command IDs moved into `ScheduleLifecycle.CreatePublishedAsync`, used by startup seeding and both importers.

## Consequences

- Quartz's calendar intervals keep wall-clock time across daylight-saving changes; fixed intervals do not. Day and week intervals in zones with daylight saving therefore do not import.
- Excluded dates are per schedule. Quartz shares calendars between triggers; the importer copies each calendar's dates into every schedule that used it.
- Second-level schedules dispatch within about a second of their due time, with the scheduler's one-second period.
