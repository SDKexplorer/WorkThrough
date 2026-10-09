using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionOperations
{
    public async Task<IReadOnlyList<QueueControl>> QueuesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, $"SELECT queue,paused,revision FROM dj_queue_controls ORDER BY queue {PageSql("500")} ");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<QueueControl>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<WorkerControlState>> WorkerControlsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, $"SELECT worker_id,draining,revision FROM dj_worker_controls ORDER BY worker_id {PageSql("500")} ");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<WorkerControlState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new(reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2)));
        }

        return rows;
    }

    public Task<int> SetQueuePausedAsync(string queue, bool paused, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        SetControlAsync("dj_queue_controls", "queue", "paused", queue, paused, expectedRevision, command, paused ? "queues.pause" : "queues.resume", cancellationToken);

    public Task<int> SetWorkerDrainingAsync(string workerId, bool draining, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        SetControlAsync("dj_worker_controls", "worker_id", "draining", workerId, draining, expectedRevision, command, draining ? "workers.drain" : "workers.resume", cancellationToken);

    private Task<int> SetControlAsync(string table, string keyColumn, string valueColumn, string key, bool enabled, int expectedRevision,
        AdministrativeCommand command, string action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || expectedRevision < 0)
        {
            throw new ArgumentException("A control needs a 1–256 character key and non-negative revision.");
        }

        return AdminAsync(command, action, key, new
        {
            enabled,
            expectedRevision
        }, async (connection, transaction, token) =>
        {
            var previous = await ScalarAsync(connection, transaction, $"SELECT revision FROM {table} WHERE {keyColumn}=@key", token, ("key", key));
            if (Convert.ToInt32(previous) != expectedRevision)
            {
                throw new StateConflictException("Control revision changed.");
            }

            var next = checked(expectedRevision + 1);
            await ExecuteAsync(connection, transaction, UpsertSql(table, $"{keyColumn},{valueColumn},revision", "@key,@value,@revision", keyColumn, $"{valueColumn},revision"), token,
                ("key", key), ("value", enabled ? 1 : 0), ("revision", next));

            return next;
        }, ct);
    }

    public Task<Guid> ReplayAsync(Guid jobId, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "jobs.replay", jobId.ToString(), new
        {
            jobId
        }, async (connection, transaction, ct) =>
        {
            var original = (await ReadJobsAsync(connection, transaction, "SELECT * FROM dj_jobs WHERE id=@id", ct, ("id", jobId.ToString()))).SingleOrDefault()
                ?? throw new StateConflictException("Execution does not exist in the active store. Export archived input explicitly before starting new work.");
            if (original.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Waiting or JobStatus.AwaitingSignal || original.Contract.Key == "workthrough.signal")
            {
                throw new StateConflictException("Only terminal processor executions can be replayed.");
            }

            var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, transaction, ct));
            var replay = original with
            {
                Id = Guid.NewGuid(),
                CausationJobId = original.Id,
                Status = JobStatus.Queued,
                Attempt = 0,
                CreatedAt = now,
                AvailableAt = now,
                ExpiresAt = null,
                IdempotencyKey = null,
                WorkflowRunId = null,
                StepId = null,
                Output = null,
                Error = null,
                WorkerId = null,
                LeaseToken = null,
                LeaseExpiresAt = null
            };
            await InsertJobAsync(connection, transaction, replay, ct);
            await JournalAsync(connection, transaction, replay, now.ToUnixTimeMilliseconds(), "Replayed", ct);

            return replay.Id;
        }, cancellationToken);

    private async Task ExpireQueuedAsync(DbConnection connection, DbTransaction transaction, long now, CancellationToken ct)
    {
        var expired = await ReadJobsAsync(connection, transaction, $"SELECT * FROM dj_jobs WHERE status='Queued' AND expires_at<=@now ORDER BY expires_at {PageSql("100")}", ct, ("now", now));
        foreach (var job in expired)
        {
            await ExecuteAsync(connection, transaction, "UPDATE dj_jobs SET status='Expired',error='The queued execution expired before admission' WHERE id=@id", ct, ("id", job.Id.ToString()));
            await JournalAsync(connection, transaction, job, now, "Expired", ct);
            if (job.WorkflowRunId is { } flow)
            {
                await AdvanceWorkflowAsync(connection, transaction, flow, ct);
            }
        }
    }
}
