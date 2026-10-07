using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    public Task<int> SaveScheduleDraftAsync(ScheduleDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default)
    {
        _ = new JobContractId(definition.Key, definition.Version);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        return AdminAsync(command, "schedules.edit", $"{definition.Key}/v{definition.Version}", new { definition, expectedRevision }, async (connection, transaction, ct) =>
        {
            var existing = (await ReadSchedulesAsync(connection, transaction, "WHERE schedule_key=@key AND version=@version", ct,
                ("key", definition.Key), ("version", definition.Version))).SingleOrDefault();
            if (existing is null ? expectedRevision != 0 : existing.Status != DefinitionStatus.Draft || existing.Revision != expectedRevision)
                throw new StateConflictException("Schedule draft changed or is already published. Create a new version to change published content.");
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_schedules(schedule_key,version,definition,status,revision,enabled) VALUES (@key,@version,@definition,'Draft',@revision,0)
                ON CONFLICT(schedule_key,version) DO UPDATE SET definition=excluded.definition,revision=excluded.revision
                """, ct, ("key", definition.Key), ("version", definition.Version), ("definition", JsonSerializer.Serialize(definition, JobJson.Options)), ("revision", checked(expectedRevision + 1)));
            return checked(expectedRevision + 1);
        }, cancellationToken);
    }

    public Task<int> PublishScheduleAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "schedules.publish", $"{key}/v{version}", new { expectedRevision }, async (connection, transaction, ct) =>
        {
            var schedule = await ExpectedScheduleAsync(connection, transaction, key, version, expectedRevision, ct);
            if (schedule.Status != DefinitionStatus.Draft) throw new StateConflictException("Only a draft schedule can be published.");
            ScheduleClock.Validate(schedule.Definition);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            var next = ScheduleClock.Next(schedule.Definition, now);
            await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET status='Published',revision=revision+1,next_due=@next WHERE schedule_key=@key AND version=@version", ct,
                ("next", next?.ToUnixTimeMilliseconds()), ("key", key), ("version", version));
            return checked(expectedRevision + 1);
        }, cancellationToken);

    public Task<int> SetScheduleEnabledAsync(string key, int version, int expectedRevision, bool enabled, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, enabled ? "schedules.enable" : "schedules.pause", $"{key}/v{version}", new { expectedRevision, enabled }, async (connection, transaction, ct) =>
        {
            var schedule = await ExpectedScheduleAsync(connection, transaction, key, version, expectedRevision, ct);
            if (schedule.Status != DefinitionStatus.Published) throw new StateConflictException("Only a published schedule can be enabled or paused.");
            if (enabled && Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM dj_schedules WHERE schedule_key=@key AND version<>@version AND enabled=1", ct, ("key", key), ("version", version))) != 0)
                throw new StateConflictException("Pause the active schedule version before enabling its replacement.");
            await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET enabled=@enabled,revision=revision+1 WHERE schedule_key=@key AND version=@version", ct,
                ("enabled", enabled ? 1 : 0), ("key", key), ("version", version));
            return checked(expectedRevision + 1);
        }, cancellationToken);

    private static async Task<StoredSchedule> ExpectedScheduleAsync(DbConnection connection, DbTransaction transaction, string key, int version, int revision, CancellationToken ct)
    {
        var schedule = (await ReadSchedulesAsync(connection, transaction, "WHERE schedule_key=@key AND version=@version", ct, ("key", key), ("version", version))).SingleOrDefault();
        if (schedule is null || schedule.Revision != revision) throw new StateConflictException("The schedule changed. Refresh its revision.");
        return schedule;
    }

    public async Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadSchedulesAsync(connection, null, "ORDER BY schedule_key,version DESC LIMIT 500", cancellationToken);
    }

    private static async Task<List<StoredSchedule>> ReadSchedulesAsync(DbConnection connection, DbTransaction? transaction, string suffix, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, "SELECT * FROM dj_schedules " + suffix, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var schedules = new List<StoredSchedule>();
        while (await reader.ReadAsync(ct)) schedules.Add(new(JsonSerializer.Deserialize<ScheduleDefinition>(Text(reader, "definition")!, JobJson.Options)!,
            Enum.Parse<DefinitionStatus>(Text(reader, "status")!), reader.GetInt32(reader.GetOrdinal("revision")), reader.GetInt32(reader.GetOrdinal("enabled")) == 1, Date(reader, "next_due")));
        return schedules;
    }

    public Task<int> DispatchDueSchedulesAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return WriteAsync(async (connection, transaction, ct) =>
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            var schedules = await ReadSchedulesAsync(connection, transaction,
                "WHERE status='Published' AND enabled=1 AND next_due<=@now ORDER BY checked_at,next_due,schedule_key,version LIMIT @limit", ct,
                ("now", now.ToUnixTimeMilliseconds()), ("limit", batchSize));
            var created = 0;
            foreach (var schedule in schedules)
            {
                var definition = schedule.Definition;
                var due = schedule.NextOccurrence!.Value;
                await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET checked_at=@now WHERE schedule_key=@key AND version=@version", ct,
                    ("now", now.ToUnixTimeMilliseconds()), ("key", definition.Key), ("version", definition.Version));
                var active = Convert.ToInt64(await ScalarAsync(connection, transaction, """
                    SELECT COUNT(*) FROM dj_schedule_occurrences o JOIN dj_jobs j ON o.job_id=j.id
                    WHERE o.schedule_key=@key AND j.status IN ('Waiting','Queued','Running')
                    """, ct, ("key", definition.Key))) > 0;
                if (active && definition.Overlap == ScheduleOverlap.Coalesce) continue; // One pending occurrence, with fair batch scanning.
                var misfired = now - due > TimeSpan.FromSeconds(30);
                var skip = active && definition.Overlap == ScheduleOverlap.SkipIfActive || misfired && definition.Misfire == ScheduleMisfire.Skip;
                Guid? id = null;
                if (!skip)
                {
                    id = Guid.NewGuid();
                    var job = new JobRecord(id.Value, definition.Processor, definition.Input, definition.Queue, definition.CorrelationId,
                        JobStatus.Queued, 0, definition.MaxAttempts, now, now);
                    await InsertJobAsync(connection, transaction, job, ct);
                    await JournalAsync(connection, transaction, job, now.ToUnixTimeMilliseconds(), "ScheduledOccurrence", ct);
                    created++;
                }
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO dj_schedule_occurrences(schedule_key,version,due,outcome,job_id) VALUES (@key,@version,@due,@outcome,@job)
                    """, ct, ("key", definition.Key), ("version", definition.Version), ("due", due.ToUnixTimeMilliseconds()),
                    ("outcome", skip ? active ? "SkippedActive" : "SkippedMisfire" : misfired ? "MisfireDispatched" : "Dispatched"), ("job", id?.ToString()));
                var next = ScheduleClock.Next(definition, definition.Misfire == ScheduleMisfire.CatchUp && definition.Overlap != ScheduleOverlap.Coalesce ? due : now);
                await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET next_due=@next WHERE schedule_key=@key AND version=@version", ct,
                    ("next", next?.ToUnixTimeMilliseconds()), ("key", definition.Key), ("version", definition.Version));
            }
            return created;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduleOccurrence>> OccurrencesAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var query = Command(connection, null, "SELECT version,due,outcome,job_id FROM dj_schedule_occurrences WHERE schedule_key=@key ORDER BY due DESC LIMIT 100", ("key", key));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        var occurrences = new List<ScheduleOccurrence>();
        while (await reader.ReadAsync(cancellationToken)) occurrences.Add(new(key, reader.GetInt32(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetString(2), ParseGuid(Text(reader, "job_id"))));
        return occurrences;
    }
}
