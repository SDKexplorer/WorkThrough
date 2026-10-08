using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    /// <summary>Providers use a read snapshot without acquiring the admission write lock.</summary>
    protected virtual Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken) =>
        connection.BeginTransactionAsync(cancellationToken).AsTask();

    public async Task<DeploymentRequirements> DeploymentRequirementsAsync(CancellationToken cancellationToken = default)
    {
        const int limit = 1000;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginReadAsync(connection, cancellationToken);
        var now = await NowAsync(connection, transaction, cancellationToken);
        var items = new Dictionary<(JobContractId, string), DeploymentRequirement>();
        var truncated = false;
        await using (var command = Command(connection, transaction, """
            SELECT j.job_key,j.contract_version,j.queue,COUNT(*) AS outstanding,
              SUM(CASE WHEN j.status='Queued' AND j.available_at<=@now AND (j.workflow_run_id IS NULL OR f.status='Running') THEN 1 ELSE 0 END) AS ready,
              MIN(CASE WHEN j.status='Queued' AND j.available_at<=@now AND (j.workflow_run_id IS NULL OR f.status='Running') THEN j.available_at ELSE NULL END) AS oldest
            FROM dj_jobs j LEFT JOIN dj_flows f ON j.workflow_run_id=f.id
            WHERE j.status IN ('Queued','Running','Waiting')
              OR (j.status IN ('Failed','Cancelled') AND f.status IN ('Running','Paused','Incident'))
            GROUP BY j.job_key,j.contract_version,j.queue ORDER BY j.job_key,j.contract_version,j.queue LIMIT @limit
            """, ("now", now), ("limit", limit + 1)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.Count == limit) { truncated = true; break; }
                var processor = new JobContractId(reader.GetString(0), reader.GetInt32(1));
                var queue = reader.GetString(2);
                items.Add((processor, queue), new(processor, queue, reader.GetInt64(3), reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), 0));
            }
        }
        await using (var command = Command(connection, transaction,
            "SELECT definition FROM dj_schedules WHERE status='Published' AND enabled=1 ORDER BY schedule_key,version LIMIT @limit", ("limit", limit + 1)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var schedules = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (++schedules > limit) { truncated = true; break; }
                var schedule = JsonSerializer.Deserialize<ScheduleDefinition>(reader.GetString(0), JobJson.Options)!;
                var key = (schedule.Processor, schedule.Queue);
                if (items.TryGetValue(key, out var existing)) items[key] = existing with { EnabledSchedules = existing.EnabledSchedules + 1 };
                else if (items.Count < limit) items.Add(key, new(schedule.Processor, schedule.Queue, 0, 0, null, 1));
                else truncated = true;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(DateTimeOffset.FromUnixTimeMilliseconds(now), items.Values.OrderBy(x => x.Processor.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Processor.Version).ThenBy(x => x.Queue, StringComparer.Ordinal).ToArray(), truncated);
    }
}
