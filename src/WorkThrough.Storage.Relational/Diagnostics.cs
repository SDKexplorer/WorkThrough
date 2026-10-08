using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    public Task<bool> FailWithDetailsAsync(Guid id, Guid leaseToken, JobFailure failure, TimeSpan retryDelay, CancellationToken cancellationToken = default) =>
        SettleAsync(id, leaseToken, null, $"{failure.ExceptionType}: {failure.Message}", retryDelay, false, cancellationToken, failure);

    public Task<bool> BeginAttemptAsync(Guid id, Guid leaseToken, TimeSpan? executionTimeout, TimeSpan? progressTimeout, CancellationToken cancellationToken = default)
    {
        if (executionTimeout <= TimeSpan.Zero || progressTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(executionTimeout));
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            if (!await OwnsAsync(connection, transaction, id, leaseToken, now, ct))
            {
                return false;
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO dj_progress(job_id,lease_token,started_at,last_progress_at,deadline,progress_timeout)
                VALUES (@id,@token,@now,@now,@deadline,@timeout)
                ON CONFLICT(job_id) DO UPDATE SET lease_token=excluded.lease_token,started_at=excluded.started_at,
                last_progress_at=excluded.last_progress_at,deadline=excluded.deadline,progress_timeout=excluded.progress_timeout,description=NULL
                """, ct, ("id", id.ToString()), ("token", leaseToken.ToString()), ("now", now),
                ("deadline", executionTimeout is { } timeout ? checked(now + (long)timeout.TotalMilliseconds) : null),
                ("timeout", progressTimeout is { } progress ? (long)progress.TotalMilliseconds : null));

            return true;
        }, cancellationToken);
    }

    public Task<bool> ReportProgressAsync(Guid id, Guid leaseToken, string? description, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction, ct) =>
        {
            var now = await NowAsync(connection, transaction, ct);
            if (!await OwnsAsync(connection, transaction, id, leaseToken, now, ct))
            {
                return false;
            }

            return await ExecuteAsync(connection, transaction,
                "UPDATE dj_progress SET last_progress_at=@now,description=@description WHERE job_id=@id AND lease_token=@token", ct,
                ("now", now), ("description", Clip(description, 500)), ("id", id.ToString()), ("token", leaseToken.ToString())) == 1;
        }, cancellationToken);

    private static async Task<bool> OwnsAsync(DbConnection connection, DbTransaction transaction, Guid id, Guid token, long now, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM dj_jobs WHERE id=@id AND lease_token=@token AND status='Running' AND lease_expires_at>@now", ct,
            ("id", id.ToString()), ("token", token.ToString()), ("now", now))) == 1;

    public async Task<IReadOnlyList<AttemptFailure>> FailuresAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT attempt,exception_type,message,stack_trace FROM dj_failures WHERE job_id=@id ORDER BY attempt", ("id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var failures = new List<AttemptFailure>();
        while (await reader.ReadAsync(cancellationToken))
        {
            failures.Add(new(reader.GetInt32(0), new(reader.GetString(1), reader.GetString(2), Text(reader, "stack_trace"))));
        }

        return failures;
    }

    public async Task<JobHealth?> HealthAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var job = (await ReadJobsAsync(connection, null, "SELECT * FROM dj_jobs WHERE id=@id", cancellationToken, ("id", id.ToString()))).SingleOrDefault();
        if (job is null)
        {
            return null;
        }

        var now = DateTimeOffset.FromUnixTimeMilliseconds(await NowAsync(connection, null, cancellationToken));
        JobProgress? progress = null;
        if (job.Status == JobStatus.Running)
        {
            await using var command = Command(connection, null, "SELECT * FROM dj_progress WHERE job_id=@id AND lease_token=@token",
                ("id", id.ToString()), ("token", job.LeaseToken?.ToString()));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                progress = new(Date(reader, "started_at")!.Value, Date(reader, "last_progress_at")!.Value,
                Date(reader, "deadline"), reader.IsDBNull(reader.GetOrdinal("progress_timeout")) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("progress_timeout"))), Text(reader, "description"));
            }
        }

        if (job.Status == JobStatus.Running && job.LeaseExpiresAt <= now)
        {
            return new("LeaseExpired", false, now, progress);
        }

        if (job.Status == JobStatus.Running && progress?.Deadline <= now)
        {
            return new("ExecutionDeadlineExceeded", true, now, progress);
        }

        if (job.Status == JobStatus.Running && progress?.ProgressTimeout is { } threshold && progress.LastProgressAt + threshold <= now)
        {
            return new("ProgressOverdue", true, now, progress);
        }

        if (job.Status is JobStatus.Queued or JobStatus.Waiting or JobStatus.AwaitingSignal)
        {
            if (job.WorkflowRunId is { } run)
            {
                var status = await ScalarAsync(connection, null, "SELECT status FROM dj_flows WHERE id=@id", cancellationToken, ("id", run.ToString()));
                if (status is "Incident" or "Paused")
                {
                    return new(status is "Incident" ? "FlowIncident" : "FlowPaused", false, now, progress);
                }
            }

            if (job.Status == JobStatus.AwaitingSignal)
            {
                return new("AwaitingSignal", false, now, progress);
            }

            if (Convert.ToInt64(await ScalarAsync(connection, null, "SELECT COUNT(*) FROM dj_queue_controls WHERE queue=@queue AND paused=1", cancellationToken, ("queue", job.Queue))) > 0)
            {
                return new("QueuePaused", false, now, progress);
            }

            if (job.Status == JobStatus.Waiting)
            {
                return new("Dependencies", false, now, progress);
            }

            if (job.AvailableAt > now)
            {
                return new(job.Attempt > 0 ? "RetryScheduled" : "Scheduled", false, now, progress);
            }

            var limited = Convert.ToInt64(await ScalarAsync(connection, null, """
                SELECT COUNT(*) FROM dj_limits l WHERE (l.job_key='*' OR COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=l.job_key),l.job_key)=COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=@key),@key)) AND l.maximum <=
                (SELECT COUNT(*) FROM dj_jobs j WHERE j.status='Running' AND j.lease_expires_at>@now AND (l.job_key='*' OR COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=j.job_key),j.job_key)=COALESCE((SELECT group_key FROM dj_processor_groups WHERE job_key=l.job_key),l.job_key)))
                """, cancellationToken, ("key", job.Contract.Key), ("now", now.ToUnixTimeMilliseconds()))) > 0;
            if (limited)
            {
                return new("SharedCapacity", false, now, progress);
            }

            var workers = await ReadWorkersAsync(connection, cancellationToken);
            var capable = workers.Where(w => w.SeenAt > now.AddSeconds(-60) && w.Processors.Contains(job.Contract) && w.Queues.Contains(job.Queue)).ToArray();
            if (capable.Length == 0)
            {
                return new("NoRecentCapableWorker", false, now, progress);
            }

            if (capable.All(w => w.Running >= w.Capacity))
            {
                return new("WorkerCapacity", false, now, progress);
            }
        }

        return new(job.Status.ToString(), false, now, progress);
    }

    public Task HeartbeatAsync(WorkerHeartbeat worker, CancellationToken cancellationToken = default) => WriteAsync(async (connection, transaction, ct) =>
    {
        var now = await NowAsync(connection, transaction, ct);
        var observed = worker with
        {
            SeenAt = DateTimeOffset.FromUnixTimeMilliseconds(now)
        };
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dj_workers(id,seen_at,details) VALUES (@id,@now,@details)
            ON CONFLICT(id) DO UPDATE SET seen_at=excluded.seen_at,details=excluded.details
            """, ct, ("id", worker.WorkerId), ("now", now), ("details", JsonSerializer.Serialize(observed, JobJson.Options)));

        return true;
    }, cancellationToken);

    public async Task<IReadOnlyList<WorkerHeartbeat>> WorkersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadWorkersAsync(connection, cancellationToken);
    }

    private static async Task<IReadOnlyList<WorkerHeartbeat>> ReadWorkersAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, null, "SELECT details FROM dj_workers ORDER BY seen_at DESC LIMIT 500");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var workers = new List<WorkerHeartbeat>();
        while (await reader.ReadAsync(ct))
        {
            workers.Add(JsonSerializer.Deserialize<WorkerHeartbeat>(reader.GetString(0), JobJson.Options)!);
        }

        return workers;
    }

    public async Task<IReadOnlyList<ExecutionEvent>> EventsAsync(Guid runId, long after = 0, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT * FROM dj_events WHERE run_id=@run AND sequence>@after ORDER BY sequence LIMIT 500",
            ("run", runId.ToString()), ("after", after));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<ExecutionEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), ParseGuid(Text(reader, "run_id")),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4), reader.GetInt32(5)));
        }

        return events;
    }
}
