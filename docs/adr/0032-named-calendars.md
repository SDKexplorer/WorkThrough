# ADR 032: named calendars

Status: accepted, implemented in schema 11 and engine protocol 10.

## Context

ADR-030 added calendars that a schedule carries in its saved definition. Holidays change every year, and operators maintain them for many schedules at once. Copying a calendar into each schedule meant saving and publishing a new version of every schedule for each change.

## Decision

A store keeps named calendars in `dj_calendars`: name, the calendar JSON and a revision. `IScheduleCalendars` lists them and saves or deletes one as an audited administrative command that names its expected revision. A schedule names calendars in `ScheduleDefinition.Calendars`, which `ExcludingCalendar(name)` appends to.

Calendars are read in the same transaction as each schedule change:

- **Publish and enable** fail while a named calendar is missing. A schedule never fires on times it was meant to skip.
- **Dispatch** combines the schedule's own calendar with the named ones before computing the next occurrence.
- **Saving a calendar** recomputes the next occurrence of every published schedule that names it. An occurrence already due is evaluated again rather than dropped, so adding a holiday stops it and removing one restores the earlier occurrence.
- **Delete** is refused while a draft or an enabled schedule names the calendar. A paused version may keep the name; enabling it checks again.

Names are 1 to 120 characters without surrounding spaces. A store holds at most 500 calendars.

A scheduler from an earlier release would ignore `Calendars` and fire on excluded dates, so schedules naming calendars need engine protocol 10 (ADR-021). Calendars without such schedules need only schema 11. Oracle and MySQL upgrade schema-10 installations by resuming after their last step; PostgreSQL and SQL Server create the table when missing.

## Consequences

- Operators edit holidays once, in the console or through the API, and every affected schedule follows immediately.
- An occurrence history still records what happened; it does not record which calendar revision decided it. The audit has each calendar change.
- Quartz.NET import still copies each trigger's calendar into its schedule. Importing them as named calendars is possible later without a format change.
