using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumSchedules = 500;
    private const int MaximumScheduleBatch = 500;
    private const int MaximumOccurrences = 100;

    public Task<Result<int>> SaveScheduleDraftAsync(ScheduleDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.EditSchedule, VersionTarget(definition.Key, definition.Version), new { definition, expectedRevision }, async (connection, transaction, ct) =>
        {
            var existing = (await ReadSchedulesAsync(connection, transaction, "WHERE schedule_key=@key AND version=@version", ct,
                ("key", definition.Key), ("version", definition.Version))).SingleOrDefault();
            if (existing is null ? expectedRevision != 0 : existing.Status != DefinitionStatus.Draft || existing.Revision != expectedRevision)
            {
                throw new StateConflictException("Schedule draft changed or is already published. Create a new version to change published content.");
            }

            var revision = checked(expectedRevision + 1);
            var upsert = UpsertSql("dj_schedules", "schedule_key,version,definition,status,revision,enabled",
                "@key,@version,@definition,@status,@revision,0", "schedule_key,version", "definition,revision");
            await ExecuteAsync(connection, transaction, upsert, ct, ("key", definition.Key), ("version", definition.Version),
                ("definition", JsonSerializer.Serialize(definition, JobJson.Options)), ("status", nameof(DefinitionStatus.Draft)), ("revision", revision));

            return revision;
        }, cancellationToken, validate: () => ValidateScheduleDraft(definition, expectedRevision));

    private static void ValidateScheduleDraft(ScheduleDefinition definition, int expectedRevision)
    {
        _ = new JobContractId(definition.Key, definition.Version);
        PartitionKeys.Validate(definition.PartitionKey, nameof(definition));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
    }

    public Task<Result<int>> PublishScheduleAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.PublishSchedule, VersionTarget(key, version), new { expectedRevision }, async (connection, transaction, ct) =>
        {
            var schedule = await ExpectedScheduleAsync(connection, transaction, key, version, expectedRevision, ct);
            if (schedule.Status != DefinitionStatus.Draft)
            {
                throw new StateConflictException("Only a draft schedule can be published.");
            }

            ScheduleClock.Validate(schedule.Definition);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            var next = ScheduleClock.Next(schedule.Definition, now);
            await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET status=@status,revision=revision+1,next_due=@next WHERE schedule_key=@key AND version=@version", ct,
                ("status", nameof(DefinitionStatus.Published)), ("next", next?.ToUnixTimeMilliseconds()), ("key", key), ("version", version));

            return checked(expectedRevision + 1);
        }, cancellationToken);

    public Task<Result<int>> SetScheduleEnabledAsync(string key, int version, int expectedRevision, bool enabled, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, enabled ? AuditActions.EnableSchedule : AuditActions.PauseSchedule, VersionTarget(key, version), new { expectedRevision, enabled }, async (connection, transaction, ct) =>
        {
            var schedule = await ExpectedScheduleAsync(connection, transaction, key, version, expectedRevision, ct);
            if (schedule.Status != DefinitionStatus.Published)
            {
                throw new StateConflictException("Only a published schedule can be enabled or paused.");
            }

            if (enabled && Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM dj_schedules WHERE schedule_key=@key AND version<>@version AND enabled=1", ct, ("key", key), ("version", version))) != 0)
            {
                throw new StateConflictException("Pause the active schedule version before enabling its replacement.");
            }

            await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET enabled=@enabled,revision=revision+1 WHERE schedule_key=@key AND version=@version", ct,
                ("enabled", enabled ? 1 : 0), ("key", key), ("version", version));

            return checked(expectedRevision + 1);
        }, cancellationToken);

    private async Task<StoredSchedule> ExpectedScheduleAsync(DbConnection connection, DbTransaction transaction, string key, int version, int revision, CancellationToken ct)
    {
        var schedule = (await ReadSchedulesAsync(connection, transaction, "WHERE schedule_key=@key AND version=@version", ct, ("key", key), ("version", version))).SingleOrDefault();
        if (schedule is null || schedule.Revision != revision)
        {
            throw new StateConflictException("The schedule changed. Refresh its revision.");
        }

        return schedule;
    }

    public Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<StoredSchedule>>(async (connection, transaction, ct) =>
        {
            return await ReadSchedulesAsync(connection, transaction, $"ORDER BY schedule_key,version DESC {PageSql(MaximumSchedules)}", ct);
        }, cancellationToken);
    }

    private async Task<List<StoredSchedule>> ReadSchedulesAsync(DbConnection connection, DbTransaction? transaction, string suffix, CancellationToken ct, params (string, object?)[] values)
    {
        await using var command = Command(connection, transaction, "SELECT * FROM dj_schedules " + suffix, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var schedules = new List<StoredSchedule>();
        while (await reader.ReadAsync(ct))
        {
            schedules.Add(new(JsonSerializer.Deserialize<ScheduleDefinition>(Text(reader, "definition")!, JobJson.Options)!,
            Enum.Parse<DefinitionStatus>(Text(reader, "status")!), reader.GetInt32(reader.GetOrdinal("revision")), reader.GetInt32(reader.GetOrdinal("enabled")) == 1, Date(reader, "next_due")));
        }

        return schedules;
    }

    public Task<int> DispatchDueSchedulesAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaximumScheduleBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            var schedules = await ReadSchedulesAsync(connection, transaction,
                $"WHERE status='Published' AND enabled=1 AND next_due<=@now ORDER BY checked_at,next_due,schedule_key,version {PageSql("@limit")}", ct,
                ("now", now.ToUnixTimeMilliseconds()), ("limit", batchSize));
            var created = 0;
            foreach (var schedule in schedules)
            {
                if (await DispatchOccurrenceAsync(connection, transaction, schedule, now, ct))
                {
                    created++;
                }
            }

            return created;
        }, cancellationToken);
    }

    /// <summary>Records one due occurrence and advances the schedule. Returns whether a job was created.</summary>
    private async Task<bool> DispatchOccurrenceAsync(DbConnection connection, DbTransaction transaction, StoredSchedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        var definition = schedule.Definition;
        var due = schedule.NextOccurrence!.Value;
        await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET checked_at=@now WHERE schedule_key=@key AND version=@version", ct,
            ("now", now.ToUnixTimeMilliseconds()), ("key", definition.Key), ("version", definition.Version));
        var active = Convert.ToInt64(await ScalarAsync(connection, transaction, """
            SELECT COUNT(*) FROM dj_schedule_occurrences o JOIN dj_jobs j ON o.job_id=j.id
            WHERE o.schedule_key=@key AND j.status IN ('Waiting','Queued','Running')
            """, ct, ("key", definition.Key))) > 0;
        if (active && definition.Overlap == ScheduleOverlap.Coalesce)
        {
            // One pending occurrence; the schedule stays due and is scanned fairly after others.
            return false;
        }

        var misfired = now - due > ScheduleClock.MisfireThreshold;
        var skipped = active && definition.Overlap == ScheduleOverlap.SkipIfActive || misfired && definition.Misfire == ScheduleMisfire.Skip;
        JobRecord? job = null;
        if (!skipped)
        {
            job = new JobRecord(Guid.NewGuid(), definition.Processor, definition.Input, definition.Queue, definition.CorrelationId,
                JobStatus.Queued, 0, definition.MaxAttempts, now, now)
            {
                PartitionKey = definition.PartitionKey
            };
            await InsertJobAsync(connection, transaction, job, ct);
            await JournalAsync(connection, transaction, job, now.ToUnixTimeMilliseconds(), ExecutionEventKinds.ScheduledOccurrence, ct);
        }

        var outcome = (skipped, active, misfired) switch
        {
            (true, true, _) => ScheduleOccurrenceOutcomes.SkippedActive,
            (true, false, _) => ScheduleOccurrenceOutcomes.SkippedMisfire,
            (false, _, true) => ScheduleOccurrenceOutcomes.MisfireDispatched,
            _ => ScheduleOccurrenceOutcomes.Dispatched
        };
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dj_schedule_occurrences(schedule_key,version,due,outcome,job_id) VALUES (@key,@version,@due,@outcome,@job)
            """, ct, ("key", definition.Key), ("version", definition.Version), ("due", due.ToUnixTimeMilliseconds()), ("outcome", outcome), ("job", job?.Id.ToString()));
        var catchUp = definition.Misfire == ScheduleMisfire.CatchUp && definition.Overlap != ScheduleOverlap.Coalesce;
        var next = ScheduleClock.Next(definition, catchUp ? due : now);
        await ExecuteAsync(connection, transaction, "UPDATE dj_schedules SET next_due=@next WHERE schedule_key=@key AND version=@version", ct,
            ("next", next?.ToUnixTimeMilliseconds()), ("key", definition.Key), ("version", definition.Version));

        return job is not null;
    }

    public Task<IReadOnlyList<ScheduleOccurrence>> OccurrencesAsync(string key, CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<ScheduleOccurrence>>(async (connection, transaction, ct) =>
        {
            await using var query = Command(connection, transaction, $"SELECT version,due,outcome,job_id FROM dj_schedule_occurrences WHERE schedule_key=@key ORDER BY due DESC {PageSql(MaximumOccurrences)}", ("key", key));
            await using var reader = await query.ExecuteReaderAsync(ct);
            var occurrences = new List<ScheduleOccurrence>();
            while (await reader.ReadAsync(ct))
            {
                occurrences.Add(new(key, reader.GetInt32(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetString(2), ParseGuid(Text(reader, "job_id"))));
            }

            return occurrences;
        }, cancellationToken);
    }
}
