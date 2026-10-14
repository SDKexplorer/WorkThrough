using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IScheduleCalendars
{
    private const int MaximumCalendars = 500;

    public Task<IReadOnlyList<NamedCalendar>> CalendarsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<NamedCalendar>>(async (connection, transaction, ct) =>
            [.. (await ReadCalendarsAsync(connection, transaction, ct)).Values.OrderBy(calendar => calendar.Name, StringComparer.Ordinal)], cancellationToken);

    public Task<Result<int>> SaveCalendarAsync(string name, ScheduleCalendar calendar, int expectedRevision, AdministrativeCommand command,
        CancellationToken cancellationToken = default) =>
        AdminAsync<int>(command, AuditActions.SaveCalendar, name, Args(("calendar", calendar), ("expectedRevision", expectedRevision)), async (connection, transaction, ct) =>
        {
            var calendars = await ReadCalendarsAsync(connection, transaction, ct);
            var current = calendars.GetValueOrDefault(name)?.Revision ?? 0;
            if (current != expectedRevision)
            {
                throw new StateConflictException("The calendar changed. Refresh its revision.");
            }

            if (current == 0 && calendars.Count >= MaximumCalendars)
            {
                throw new StateConflictException($"A store holds at most {MaximumCalendars} calendars.");
            }

            var revision = checked(expectedRevision + 1);
            var definition = JobJson.Serialize(calendar);
            if (current == 0)
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_calendars(name,definition,revision) VALUES (@name,@definition,@revision)", ct,
                    ("name", name), ("definition", definition), ("revision", revision));
            }
            else
            {
                await ExecuteAsync(connection, transaction, "UPDATE dj_calendars SET definition=@definition,revision=@revision WHERE name=@name AND revision=@expected", ct,
                    ("definition", definition), ("revision", revision), ("name", name), ("expected", expectedRevision));
            }

            calendars[name] = new(name, calendar, revision);
            await MoveSchedulesNamingAsync(connection, transaction, name, calendars, ct);

            return revision;
        }, cancellationToken, validate: () =>
        {
            NamedCalendar.ValidateName(name);
            ArgumentNullException.ThrowIfNull(calendar);
            calendar.Validate();
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        });

    public Task<Result> DeleteCalendarAsync(string name, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.DeleteCalendar, name, Args(("expectedRevision", expectedRevision)), async (connection, transaction, ct) =>
        {
            // A paused published version may keep the name: enabling it again checks that its calendars exist.
            if ((await ReadSchedulesAsync(connection, transaction, "", ct)).FirstOrDefault(schedule => Names(schedule.Definition, name)
                && (schedule.Status == DefinitionStatus.Draft || schedule.Status == DefinitionStatus.Published && schedule.Enabled)) is { } user)
            {
                throw new StateConflictException($"Schedule {user.Definition.Key} version {user.Definition.Version} names this calendar. Pause it first.");
            }

            if (await ExecuteAsync(connection, transaction, "DELETE FROM dj_calendars WHERE name=@name AND revision=@expected", ct,
                ("name", name), ("expected", expectedRevision)) != 1)
            {
                throw new StateConflictException("The calendar changed or does not exist. Refresh its revision.");
            }
        }, cancellationToken, validate: () => NamedCalendar.ValidateName(name));

    /// <summary>
    /// The definition with the named calendars it references applied, read in the caller's transaction. A missing calendar is a conflict:
    /// the schedule would otherwise fire on the times it was meant to skip.
    /// </summary>
    private async Task<ScheduleDefinition> ResolveCalendarsAsync(DbConnection connection, DbTransaction transaction, ScheduleDefinition definition,
        CancellationToken ct)
    {
        if (definition.Calendars is not { Count: > 0 })
        {
            return definition;
        }

        var calendars = await ReadCalendarsAsync(connection, transaction, ct);

        return definition.Calendars.FirstOrDefault(name => !calendars.ContainsKey(name)) is { } missing
            ? throw new StateConflictException($"Schedule {definition.Key} v{definition.Version} names calendar '{missing}', which does not exist.")
            : ScheduleClock.WithCalendars(definition, Calendars(calendars));
    }

    /// <summary>
    /// Re-evaluates the next occurrence of each published schedule naming <paramref name="name"/>. An occurrence already due is checked
    /// again rather than dropped, so a newly excluded date stops it and a removed exclusion brings an earlier one back.
    /// </summary>
    private async Task MoveSchedulesNamingAsync(DbConnection connection, DbTransaction transaction, string name, Dictionary<string, NamedCalendar> calendars,
        CancellationToken ct)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
        var schedules = await ReadSchedulesAsync(connection, transaction, "WHERE status='Published'", ct);
        foreach (var schedule in schedules.Where(schedule => Names(schedule.Definition, name)))
        {
            var from = schedule.NextOccurrence is { } due && due <= now ? due.AddTicks(-1) : now;
            var next = ScheduleClock.Next(ScheduleClock.WithCalendars(schedule.Definition, Calendars(calendars)), from);
            await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET next_due=@next WHERE schedule_key=@key AND version=@version", ct,
                ("next", next?.ToUnixTimeMilliseconds()), ("key", schedule.Definition.Key), ("version", schedule.Definition.Version));
        }
    }

    private async Task<Dictionary<string, NamedCalendar>> ReadCalendarsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT name,definition,revision FROM dj_calendars");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var calendars = new Dictionary<string, NamedCalendar>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            calendars[name] = new(name, JobJson.Deserialize<ScheduleCalendar>(Text(reader, "definition")!)!, reader.GetInt32(2));
        }

        return calendars;
    }

    private static Dictionary<string, ScheduleCalendar> Calendars(Dictionary<string, NamedCalendar> calendars) =>
        calendars.ToDictionary(pair => pair.Key, pair => pair.Value.Calendar, StringComparer.Ordinal);

    private static bool Names(ScheduleDefinition definition, string name) => definition.Calendars?.Contains(name, StringComparer.Ordinal) == true;
}
